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
    // vkvideo.ru держит собственные cookie на своём домене — без него не выпустить
    // web-токен приложения live-SDK.
    private static readonly string[] CookieHosts = ["vk.ru", "login.vk.ru", "web.api.vk.ru", "vkvideo.ru"];

    private readonly VkSession _session;
    private readonly VkClientOptions _options;
    private readonly HttpClient _http;
    private readonly HttpClient _uploadHttp;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    public VkWebApi(VkSession session, VkClientOptions options)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Ёмкости подняты намеренно: у VK много remix*-cookie, и мы дублируем их по трём хостам.
        // Дефолтные лимиты (20 на домен, 300 всего) могли бы молча вытеснить remixsid.
        var container = new CookieContainer(capacity: 10_000, perDomainCapacity: 1_000, maxCookieSize: 8192);
        PopulateCookies(container, session.Cookies ?? []);

        var handler = options.ApiHttpMessageHandlerFactory?.Invoke() ?? new HttpClientHandler
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

        // Отдельный клиент для загрузки медиа: подписанные upload-URL не требуют cookies,
        // таймаут больше (видео/файлы грузятся дольше), и НЕ следуем редиректам:
        // часть upload-серверов pu.vk.ru отвечает 3xx, следование которому меняет POST→GET и даёт 405.
        var uploadHandler = options.UploadHttpMessageHandlerFactory?.Invoke() ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _uploadHttp = new HttpClient(uploadHandler, disposeHandler: true) { Timeout = options.UploadTimeout };
        _uploadHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", session.UserAgent ?? _options.UserAgent);
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
    /// Загрузить файл (multipart/form-data) на подписанный upload-URL VK и вернуть JSON-ответ.
    /// Токен/куки не требуются: URL уже подписан. Имя поля зависит от типа медиа
    /// (photo — фото, file — документ, video_file — видео; всё проверено на живых серверах).
    /// Вызывающий обязан вызвать Dispose у возвращённого документа.
    /// </summary>
    public Task<JsonDocument> UploadFileAsync(
        string uploadUrl, string fieldName, byte[] bytes, string fileName, string contentType,
        CancellationToken cancellationToken = default) =>
        UploadFileAsync(
            uploadUrl,
            fieldName,
            VkUploadSource.FromBytes(bytes, fileName, contentType),
            cancellationToken);

    /// <summary>
    /// Потоково загрузить повторно открываемый файл на подписанный upload-URL VK.
    /// Файл не буферизуется целиком; при ретрае вызывающий код повторно открывает источник.
    /// </summary>
    public async Task<JsonDocument> UploadFileAsync(
        string uploadUrl,
        string fieldName,
        VkUploadSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(source);

        await using var sourceStream = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(sourceStream, bufferSize: 128 * 1024);
        file.Headers.ContentType = new MediaTypeHeaderValue(source.ContentType);
        file.Headers.ContentLength = source.Length;
        content.Add(file, fieldName, source.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
        using var response = await _uploadHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new VkClientException(
                $"Сервер загрузки вернул HTTP {(int)response.StatusCode}: {VkSafeErrorDetails.Describe(body)}");

        try { return JsonDocument.Parse(body); }
        catch (JsonException)
        {
            throw new VkClientException(
                $"Сервер загрузки вернул не-JSON ответ: {VkSafeErrorDetails.Describe(body)}");
        }
    }

    /// <summary>Загрузить изображение на сервер фото VK (URL из photos.get*UploadServer). Поле формы — «photo».</summary>
    public Task<PhotoUploadResult> UploadPhotoAsync(string uploadUrl, VkImage image, CancellationToken cancellationToken = default)
        => UploadPhotoAsync(
            uploadUrl,
            VkUploadSource.FromBytes(image.Bytes, image.FileName, image.ContentType),
            cancellationToken);

    /// <summary>Потоково загрузить изображение на сервер фото VK. Поле формы — «photo».</summary>
    public async Task<PhotoUploadResult> UploadPhotoAsync(
        string uploadUrl,
        VkUploadSource image,
        CancellationToken cancellationToken = default)
    {
        using var doc = await UploadFileAsync(uploadUrl, "photo", image, cancellationToken)
                              .ConfigureAwait(false);
        var root = doc.RootElement;
        var photo = root.TryGetProperty("photo", out var p) ? p.GetString() : null;
        // Пустой "photo" ("" или "[]") = файл не принят (обычно слишком маленькое изображение).
        if (string.IsNullOrEmpty(photo) || photo == "[]")
            throw new VkClientException("Сервер загрузки не принял изображение (слишком маленькое или неподдерживаемый формат).");

        return new PhotoUploadResult(
            Server: root.TryGetProperty("server", out var s) && s.TryGetInt64(out var sv) ? sv : 0,
            Photo: photo,
            Hash: root.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "");
    }

    /// <summary>Достаёт поле "response" или бросает <see cref="VkClientException"/> (не роняет KeyNotFoundException).</summary>
    public static JsonElement GetResponseOrThrow(JsonDocument doc, string method)
    {
        if (!doc.RootElement.TryGetProperty("response", out var response))
        {
            throw new VkClientException(
                $"Ответ '{method}' не содержит поля 'response': {VkSafeErrorDetails.Describe(doc.RootElement)}");
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
        var minted = await MintAsync(
            _options.WebAppId,
            $"{_options.LoginBaseUrl}/?act=web_token",
            _options.WebBaseUrl,
            bearer: null,
            cancellationToken).ConfigureAwait(false);

        _session.WebToken = minted.AccessToken;
        _session.WebTokenExpiresAtUnix = minted.ExpiresAtUnix;
        _session.UserId = minted.UserId ?? _session.UserId;
        _session.LogoutHash = minted.LogoutHash ?? _session.LogoutHash;
    }

    /// <summary>
    /// Выпустить web-токен приложения live-SDK, не трогая токен сессии: его нельзя класть
    /// в <see cref="VkSession.WebToken"/>, иначе им начнут ходить в методы web.api.vk.ru,
    /// для которых он не выпускался.
    ///
    /// Выпуск идёт на vkvideo.ru, а не на login.vk.ru: последний отвечает <c>type=error</c>
    /// на этот app_id, хотя для мессенджера в той же сессии выдаёт токен. Запрос повторяет
    /// то, что делает страница: уже выпущенный web-токен передаётся в теле.
    /// </summary>
    internal async Task<MintedWebToken> MintWebTokenForAppAsync(string appId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        // Нужен действующий токен мессенджера — он идёт в тело запроса как access_token.
        await EnsureWebTokenAsync(cancellationToken).ConfigureAwait(false);

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MintAsync(
                appId,
                _options.LiveSdkWebTokenUrl,
                _options.LiveSdkWebBaseUrl,
                _session.WebToken,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<MintedWebToken> MintAsync(
        string appId,
        string url,
        string origin,
        string? bearer,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["version"] = "1",
            ["app_id"] = appId,
        };
        if (!string.IsNullOrEmpty(bearer))
            form["access_token"] = bearer;

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        AddWebHeaders(request, origin);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var host = new Uri(url).Host;
        if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.MovedPermanently)
        {
            // Редирект на вход означает разное в зависимости от того, есть ли вообще cookie этого
            // домена. У сессий, снятых до появления live-SDK, их нет — и «сессия истекла» тут
            // отправило бы искать несуществующую проблему.
            throw new VkSessionExpiredException(HasCookiesFor(host)
                ? $"{host} перенаправляет на страницу входа — cookie-сессия истекла."
                : $"В сессии нет cookie для {host}, поэтому выпустить там web-токен нельзя. " +
                  "Войдите заново: при входе клиент заходит на этот домен и снимает его cookie. " +
                  "Отдельной авторизации это не требует — домены VK связаны SSO.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new VkSessionExpiredException(
                "Не удалось разобрать ответ web_token (вероятно, вернулась HTML-страница входа). Нужен повторный вход.");
        }

        using (doc)
        {
            var root = doc.RootElement;

            // login.vk.ru отвечает {"type":"okay","data":{…}}; у vkvideo.ru обёртка своя,
            // поэтому payload ищем и там, и там, а не полагаемся на одну форму.
            var payload = ExtractPayload(root);
            var accessToken = payload is { } p && p.TryGetProperty("access_token", out var at)
                ? at.GetString()
                : null;

            if (string.IsNullOrEmpty(accessToken))
            {
                throw new VkSessionExpiredException(
                    $"{host} не выдал web-токен для app_id={appId}. " +
                    "Либо сессия недействительна и нужен повторный вход, либо это приложение здесь не обслуживается. " +
                    VkSafeErrorDetails.Describe(root));
            }

            var data = payload!.Value;
            return new MintedWebToken(
                accessToken,
                data.TryGetProperty("expires", out var exp) && exp.TryGetInt64(out var e) ? e : 0,
                data.TryGetProperty("user_id", out var uid) && uid.TryGetInt64(out var u) ? u : null,
                data.TryGetProperty("logout_hash", out var lh) ? lh.GetString() : null);
        }
    }

    /// <summary>
    /// Находит объект с токеном в ответе web_token. Формы отличаются по хостам, поэтому
    /// проверяются известные обёртки, а затем — сам корень.
    /// </summary>
    private static JsonElement? ExtractPayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        // login.vk.ru: успех помечен type=okay, и только тогда data осмысленна.
        if (root.TryGetProperty("type", out var type) && type.GetString() != "okay")
            return null;

        foreach (var wrapper in new[] { "data", "response", "payload" })
        {
            if (root.TryGetProperty(wrapper, out var nested) && nested.ValueKind == JsonValueKind.Object)
                return nested;
        }

        return root.TryGetProperty("access_token", out _) ? root : null;
    }

    /// <summary>Разобранный ответ web_token. Токен — секрет, в логи не писать.</summary>
    internal sealed record MintedWebToken(string AccessToken, long ExpiresAtUnix, long? UserId, string? LogoutHash);

    /// <summary>Есть ли в сессии хоть одна cookie, чей домен покрывает указанный хост.</summary>
    private bool HasCookiesFor(string host) =>
        _session.Cookies?.Any(cookie =>
        {
            var domain = (cookie.Domain ?? string.Empty).TrimStart('.');
            return domain.Length > 0
                   && (host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                       || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        }) ?? false;

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
        catch (JsonException)
        {
            throw new VkClientException(
                $"Метод '{method}' вернул не-JSON ответ (HTTP {(int)response.StatusCode}): " +
                VkSafeErrorDetails.Describe(body));
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

    private void AddWebHeaders(HttpRequestMessage request) => AddWebHeaders(request, _options.WebBaseUrl);

    private static void AddWebHeaders(HttpRequestMessage request, string origin)
    {
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Referer", origin + "/");
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

    public void Dispose()
    {
        _http.Dispose();
        _uploadHttp.Dispose();
        _tokenGate.Dispose();
    }
}
