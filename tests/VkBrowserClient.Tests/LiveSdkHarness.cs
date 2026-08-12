using System.Net;
using System.Text;

namespace VkBrowserClient.Tests;

/// <summary>
/// Общая обвязка для тестов live-SDK: сессия с уже валидным web-токеном, клиент с двумя
/// подменёнными транспортами (login.vk.ru и apisdk.live.vkvideo.ru) и разбор запроса.
/// </summary>
internal static class LiveSdkHarness
{
    /// <summary>Ответ login.vk.ru на выпуск web-токена для приложения live-SDK.</summary>
    public const string WebTokenMinted =
        """{"type":"okay","data":{"access_token":"vk1.a.live-web-token","expires":1234,"user_id":42}}""";

    /// <summary>Ответ обмена на SDK-токен.</summary>
    public const string SdkTokenIssued =
        """{"access_token":"sdk-token","refresh_token":"sdk-refresh","expires_in":2592000}""";

    public static VkSession Session() => new()
    {
        UserId = 42,
        WebToken = "test-token",
        WebTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        Cookies =
        [
            new VkCookie { Name = "remixsid", Value = "secret", Domain = ".vk.ru", Secure = true, HttpOnly = true }
        ]
    };

    /// <summary>Сессия с уже выпущенным SDK-токеном — когда выпуск в тесте не интересен.</summary>
    public static VkSession SessionWithSdkToken(string token = "sdk-token")
    {
        var session = Session();
        session.LiveSdkToken = token;
        session.LiveSdkDeviceId = "test-device";
        session.LiveSdkTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds();
        return session;
    }

    public static VkClient Client(VkSession session, HttpMessageHandler sdk, HttpMessageHandler? api = null) =>
        new(new MemorySessionStore(session), new VkClientOptions
        {
            ApiHttpMessageHandlerFactory = () => api ?? Handler((_, _) => Task.FromResult(Json(WebTokenMinted))),
            LiveSdkHttpMessageHandlerFactory = () => sdk,
            UploadHttpMessageHandlerFactory = () => Handler(
                (_, _) => throw new InvalidOperationException("Upload не ожидался.")),
        });

    public static RecordingHandler Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(send);

    public static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static bool IsTokenExchange(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("standalone", StringComparison.Ordinal);

    public static async Task<IReadOnlyDictionary<string, string>> FormAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null)
            return new Dictionary<string, string>();

        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .ToDictionary(
                item => Uri.UnescapeDataString(item[0].Replace('+', ' ')),
                item => item.Length == 2 ? Uri.UnescapeDataString(item[1].Replace('+', ' ')) : "");
    }

    internal sealed record SdkCall(
        string Path,
        IReadOnlyDictionary<string, string> Form,
        IReadOnlyDictionary<string, string> Headers,
        string? Authorization)
    {
        public static async Task<SdkCall> FromAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => new(
                request.RequestUri!.AbsolutePath,
                await FormAsync(request, cancellationToken),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                request.Headers.Authorization?.ToString());
    }

    internal sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    internal sealed class MemorySessionStore(VkSession session) : ISessionStore
    {
        private VkSession? _session = session;

        public Task<VkSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_session);

        public Task SaveAsync(VkSession session, CancellationToken cancellationToken = default)
        {
            _session = session;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _session = null;
            return Task.CompletedTask;
        }
    }
}
