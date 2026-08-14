using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Низкоуровневый доступ к live-SDK VK Видео (apisdk.live.vkvideo.ru) — тому самому API,
/// которым пользуется веб-страница трансляций.
///
/// Отличается от <see cref="VkWebApi"/> двумя вещами, и обе принципиальны:
///  1. авторизация — не cookie, а <c>Authorization: Bearer</c>; cookie сюда не отправляются вовсе;
///  2. токен живёт ~30 суток, а не 18 минут, и выпускается в два шага:
///     web-токен приложения live-SDK → обмен на <c>oauth/vk/token/standalone</c>.
///
/// Первый шаг делегирован <see cref="VkWebApi.MintWebTokenForAppAsync"/> — там уже есть
/// вся возня с cookie-сессией.
/// </summary>
public sealed class VkLiveSdkApi : IDisposable
{
    private readonly VkSession _session;
    private readonly VkClientOptions _options;
    private readonly VkWebApi _webApi;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    internal VkLiveSdkApi(VkSession session, VkClientOptions options, VkWebApi webApi)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _webApi = webApi ?? throw new ArgumentNullException(nameof(webApi));

        // Cookie здесь не нужны и намеренно не отправляются: браузер ходит сюда с
        // withCredentials: false, авторизует только Bearer.
        var handler = options.LiveSdkHttpMessageHandlerFactory?.Invoke() ?? new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
        };

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", session.UserAgent ?? _options.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
    }

    /// <summary>Гарантирует действующий SDK-токен, выпуская его при необходимости.</summary>
    public async Task EnsureTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsTokenValid())
            return;

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsTokenValid())
                return;
            await IssueTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>
    /// Вызвать метод live-SDK и вернуть содержимое поля <c>data</c>.
    /// Вызывающий обязан вызвать Dispose у возвращённого документа.
    /// </summary>
    public async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? form = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var body = await SendForSuccessCoreAsync(method, path, form, cancellationToken).ConfigureAwait(false);
        return ParseData(body, $"{method} {path}");
    }

    /// <summary>
    /// Выполнить изменяющий запрос, для которого успешный live-SDK может вернуть пустой ответ
    /// или JSON без конверта <c>data</c>. HTTP-успех всё равно должен подтверждаться отдельным GET.
    /// </summary>
    internal async Task SendForSuccessAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? form = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ = await SendForSuccessCoreAsync(method, path, form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendForSuccessCoreAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? form,
        CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);

        var (status, body) = await SendRawAsync(method, path, form, cancellationToken).ConfigureAwait(false);

        // 401 — токен отозвали раньше срока (смена пароля, выход из сессии). Выпускаем заново и повторяем один раз.
        if (status == HttpStatusCode.Unauthorized)
        {
            await ForceIssueTokenAsync(cancellationToken).ConfigureAwait(false);
            (status, body) = await SendRawAsync(method, path, form, cancellationToken).ConfigureAwait(false);
            if (status == HttpStatusCode.Unauthorized)
                throw new VkSessionExpiredException(
                    $"live-SDK отклонил токен даже после перевыпуска ({method} {path}). Нужен повторный вход.");
        }

        if (!IsSuccess(status))
            throw new VkClientException(
                $"live-SDK вернул HTTP {(int)status} на {method} {path}: {VkSafeErrorDetails.Describe(body)}");

        return body;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendRawAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildUrl(path));
        if (form is not null)
            request.Content = new FormUrlEncodedContent(form);
        AddSdkHeaders(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.LiveSdkToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    // --- выпуск токена -------------------------------------------------------

    private async Task ForceIssueTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IssueTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>
    /// Выпускает SDK-токен с нуля: web-токен приложения live-SDK → обмен на standalone-токен.
    ///
    /// refresh_token сохраняется, но не используется: эндпоинт обновления не наблюдался,
    /// а браузер на каждой загрузке страницы просто проходит оба шага заново.
    /// </summary>
    private async Task IssueTokenAsync(CancellationToken cancellationToken)
    {
        var webToken = await _webApi
            .MintWebTokenForAppAsync(_options.LiveSdkAppId, cancellationToken)
            .ConfigureAwait(false);

        var deviceId = EnsureDeviceId();

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl("/oauth/vk/token/standalone"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["access_token"] = webToken.AccessToken,
                ["device_id"] = deviceId,
                ["device_os"] = "web",
                ["app_id"] = _options.LiveSdkAppId,
            }),
        };
        AddSdkHeaders(request);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!IsSuccess(response.StatusCode))
            throw new VkClientException(
                $"Обмен web-токена на SDK-токен не удался (HTTP {(int)response.StatusCode}): " +
                VkSafeErrorDetails.Describe(body));

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new VkClientException(
                "Обмен на SDK-токен вернул не-JSON ответ: " + VkSafeErrorDetails.Describe(body));
        }

        using (doc)
        {
            // Наблюдался плоский ответ, но все остальные ручки этого хоста заворачивают тело в "data".
            // Принимаем оба варианта, чтобы обёртка не ломала выпуск токена.
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                root = wrapped;

            var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(accessToken))
                throw new VkClientException(
                    "Ответ обмена не содержит access_token: " + VkSafeErrorDetails.Describe(root));

            var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var seconds)
                ? seconds
                : 0;

            _session.LiveSdkToken = accessToken;
            _session.LiveSdkRefreshToken = root.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString()
                : null;
            _session.LiveSdkTokenExpiresAtUnix = expiresIn > 0
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds()
                : 0;
        }
    }

    private string EnsureDeviceId()
    {
        if (!string.IsNullOrWhiteSpace(_session.LiveSdkDeviceId))
            return _session.LiveSdkDeviceId;

        _session.LiveSdkDeviceId = Guid.NewGuid().ToString();
        return _session.LiveSdkDeviceId;
    }

    private bool IsTokenValid()
    {
        if (string.IsNullOrEmpty(_session.LiveSdkToken) || _session.LiveSdkTokenExpiresAtUnix <= 0)
            return false;
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(_session.LiveSdkTokenExpiresAtUnix);
        return DateTimeOffset.UtcNow < expiresAt - _options.LiveSdkTokenExpirySkew;
    }

    // --- вспомогательное -----------------------------------------------------

    private string BuildUrl(string path) =>
        $"{_options.LiveSdkBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private void AddSdkHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-App", _options.LiveSdkAppHeader);
        request.Headers.TryAddWithoutValidation("X-SDK-App", _options.LiveSdkClientHeader);
        request.Headers.TryAddWithoutValidation("X-From-Id", EnsureDeviceId());
        request.Headers.TryAddWithoutValidation("X-Super-Referer", string.Empty);
        request.Headers.TryAddWithoutValidation("Origin", "https://vkvideo.ru");
        request.Headers.TryAddWithoutValidation("Referer", "https://vkvideo.ru/");
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static JsonDocument ParseData(string body, string what)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new VkClientException($"{what} вернул не-JSON ответ: {VkSafeErrorDetails.Describe(body)}");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data))
                throw new VkClientException(
                    $"Ответ {what} не содержит поля 'data': {VkSafeErrorDetails.Describe(doc.RootElement)}");

            // JsonDocument нельзя «отрезать» по элементу, поэтому переразбираем поддерево.
            return JsonDocument.Parse(data.GetRawText());
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenGate.Dispose();
    }
}
