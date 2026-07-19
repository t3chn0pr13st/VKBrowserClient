using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Низкоуровневый доступ к веб-API vk.ru поверх сохранённой сессии.
///
/// Делает ровно две вещи, которые делает браузер:
///  1. Мятит короткоживущий web-токен из cookie-сессии (POST login.vk.ru/?act=web_token);
///  2. Вызывает методы на web.api.vk.ru/method/* с этим токеном,
///     автоматически обновляя его при истечении.
///
/// Все изменения токена пишутся в переданный экземпляр <see cref="VkSession"/>,
/// поэтому вызывающему коду достаточно сохранить сессию после операций.
/// </summary>
public sealed class VkWebApi : IDisposable
{
    private static readonly string[] CookieHosts = ["vk.ru", "login.vk.ru", "web.api.vk.ru"];

    private readonly VkSession _session;
    private readonly VkClientOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    public VkWebApi(VkSession session, VkClientOptions options)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Ёмкости подняты намеренно: у VK много remix*-cookie, и мы дублируем их по трём хостам.
        // Дефолтные лимиты (20 на домен, 300 всего) могли бы молча вытеснить remixsid.
        var container = new CookieContainer(capacity: 10_000, perDomainCapacity: 1_000, maxCookieSize: 8192);
        PopulateCookies(container, session.Cookies ?? []);

        var handler = new HttpClientHandler
        {
            CookieContainer = container,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false, // редирект на страницу логина = сессия истекла, не следуем ему
        };

        _http = new HttpClient(handler, disposeHandler: true);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", session.UserAgent ?? _options.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Гарантирует наличие валидного web-токена (обновляет при необходимости).</summary>
    public async Task EnsureWebTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsTokenValid())
            return;

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsTokenValid())
                return;
            await RefreshWebTokenCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>
    /// Вызвать метод API. Возвращает JSON-документ всего ответа (свойство "response").
    /// Вызывающий обязан вызвать Dispose у возвращённого документа.
    /// </summary>
    public async Task<JsonDocument> CallAsync(
        string method,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureWebTokenAsync(cancellationToken).ConfigureAwait(false);

        var doc = await InvokeRawAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        if (TryGetError(doc, out var code, out var msg))
        {
            // Код 5 = проблема авторизации (в т.ч. «токен истёк»): обновляем токен и повторяем один раз.
            if (code == 5)
            {
                doc.Dispose();
                await ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
                doc = await InvokeRawAsync(method, parameters, cancellationToken).ConfigureAwait(false);
                if (TryGetError(doc, out var code2, out var msg2))
                {
                    doc.Dispose();
                    if (code2 == 5)
                        throw new VkSessionExpiredException(
                            $"Токен не удалось использовать даже после обновления: {msg2}. Нужен повторный вход.");
                    throw new VkApiException(method, code2, msg2);
                }
                return doc;
            }

            doc.Dispose();
            throw new VkApiException(method, code, msg);
        }

        return doc;
    }

    /// <summary>
    /// Загрузить изображение на сервер загрузки VK (URL из photos.get*UploadServer).
    /// Поле формы — «photo» (проверено на живом сервере). Токен/куки не требуются: URL уже подписан.
    /// </summary>
    public async Task<PhotoUploadResult> UploadPhotoAsync(string uploadUrl, VkImage image, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(image.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        content.Add(file, "photo", image.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex) { throw new VkClientException($"Сервер загрузки вернул не-JSON ответ: {Trim(body)}", ex); }

        using (doc)
        {
            var root = doc.RootElement;
            var photo = root.TryGetProperty("photo", out var p) ? p.GetString() : null;
            // Пустой "photo" ("" или "[]") = файл не принят (обычно слишком маленькое изображение).
            if (string.IsNullOrEmpty(photo) || photo == "[]")
                throw new VkClientException(
                    "Сервер загрузки не принял изображение (слишком маленькое или неподдерживаемый формат).");

            return new PhotoUploadResult(
                Server: root.TryGetProperty("server", out var s) && s.TryGetInt64(out var sv) ? sv : 0,
                Photo: photo,
                Hash: root.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "");
        }
    }

    /// <summary>Достаёт поле "response" или бросает <see cref="VkClientException"/> (не роняет KeyNotFoundException).</summary>
    public static JsonElement GetResponseOrThrow(JsonDocument doc, string method)
    {
        if (!doc.RootElement.TryGetProperty("response", out var response))
        {
            var raw = doc.RootElement.GetRawText();
            throw new VkClientException(
                $"Ответ '{method}' не содержит поля 'response': {(raw.Length > 300 ? raw[..300] + "…" : raw)}");
        }
        return response;
    }

    private async Task ForceRefreshAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshWebTokenCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    // --- web_token ---------------------------------------------------------

    private async Task RefreshWebTokenCoreAsync(CancellationToken cancellationToken)
    {
        var url = $"{_options.LoginBaseUrl}/?act=web_token";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["version"] = "1",
                ["app_id"] = _options.WebAppId,
            }),
        };
        AddWebHeaders(request);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.MovedPermanently)
            throw new VkSessionExpiredException("login.vk.ru перенаправляет на страницу входа — cookie-сессия истекла.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new VkSessionExpiredException(
                "Не удалось разобрать ответ web_token (вероятно, вернулась HTML-страница входа). Нужен повторный вход.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "okay" || !root.TryGetProperty("data", out var data))
            {
                var detail = root.TryGetProperty("error", out var err) ? err.ToString() : body;
                throw new VkSessionExpiredException(
                    $"login.vk.ru не выдал web-токен (type='{type}'). Сессия недействительна, нужен повторный вход. Ответ: {Trim(detail)}");
            }

            var accessToken = data.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(accessToken))
                throw new VkSessionExpiredException("Ответ web_token не содержит access_token. Нужен повторный вход.");

            _session.WebToken = accessToken;
            _session.WebTokenExpiresAtUnix = data.TryGetProperty("expires", out var exp) && exp.TryGetInt64(out var e) ? e : 0;
            _session.UserId = data.TryGetProperty("user_id", out var uid) && uid.TryGetInt64(out var u) ? u : _session.UserId;
            _session.LogoutHash = data.TryGetProperty("logout_hash", out var lh) ? lh.GetString() : _session.LogoutHash;
        }
    }

    private bool IsTokenValid()
    {
        if (string.IsNullOrEmpty(_session.WebToken) || _session.WebTokenExpiresAtUnix <= 0)
            return false;
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(_session.WebTokenExpiresAtUnix);
        return DateTimeOffset.UtcNow < expiresAt - _options.TokenExpirySkew;
    }

    // --- method call -------------------------------------------------------

    private async Task<JsonDocument> InvokeRawAsync(
        string method,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUrl}/method/{method}?v={_options.ApiVersion}&client_id={_options.WebAppId}";

        var form = new Dictionary<string, string>();
        if (parameters is not null)
            foreach (var kv in parameters)
                form[kv.Key] = kv.Value;
        form["v"] = _options.ApiVersion;
        form["access_token"] = _session.WebToken ?? string.Empty;

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        AddWebHeaders(request);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new VkClientException($"Метод '{method}' вернул не-JSON ответ (HTTP {(int)response.StatusCode}): {Trim(body)}", ex);
        }
    }

    private static bool TryGetError(JsonDocument doc, out int code, out string message)
    {
        code = 0;
        message = string.Empty;
        if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return false;

        code = error.TryGetProperty("error_code", out var c) && c.TryGetInt32(out var cc) ? cc : -1;
        message = error.TryGetProperty("error_msg", out var m) ? m.GetString() ?? "" : "";
        return true;
    }

    private void AddWebHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Origin", _options.WebBaseUrl);
        request.Headers.TryAddWithoutValidation("Referer", _options.WebBaseUrl + "/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    private static void PopulateCookies(CookieContainer container, IEnumerable<VkCookie> cookies)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var ck in cookies)
        {
            if (string.IsNullOrEmpty(ck.Name))
                continue;

            // Пропускаем уже истёкшие персистентные cookies.
            if (ck.Expires is > 0)
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds((long)ck.Expires.Value);
                if (exp <= now)
                    continue;
            }

            var cookieDomain = ck.Domain.TrimStart('.');
            foreach (var host in CookieHosts)
            {
                // Домен cookie покрывает host, если host == домен или host — его поддомен.
                if (!host.Equals(cookieDomain, StringComparison.OrdinalIgnoreCase)
                    && !host.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase))
                    continue;

                var cookie = new Cookie(ck.Name, ck.Value)
                {
                    Path = string.IsNullOrEmpty(ck.Path) ? "/" : ck.Path,
                    Secure = ck.Secure,
                };
                try
                {
                    // Привязываем к конкретному хосту — так избегаем причуд .NET с точечными доменами.
                    container.Add(new Uri($"https://{host}/"), cookie);
                }
                catch (CookieException)
                {
                    // Некорректная cookie — пропускаем, остальные важнее.
                }
            }
        }
    }

    private static string Trim(string? s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s[..300] + "…" : s);

    public void Dispose()
    {
        _http.Dispose();
        _tokenGate.Dispose();
    }
}
