using System.Net;
using System.Text;

namespace VkBrowserClient.Tests;

public sealed class VkLiveSdkApiTests
{
    [Fact]
    public async Task Issues_sdk_token_through_the_live_app_and_persists_it()
    {
        var mints = new List<IReadOnlyDictionary<string, string>>();
        var sdkCalls = new List<SdkCall>();
        var session = Session();

        await using var client = Client(
            session,
            api: Handler(async (request, ct) =>
            {
                mints.Add(await Form(request, ct));
                return Json("""{"type":"okay","data":{"access_token":"vk1.a.live-web-token","expires":1234,"user_id":42}}""");
            }),
            sdk: Handler(async (request, ct) =>
            {
                sdkCalls.Add(await SdkCall.FromAsync(request, ct));
                return request.RequestUri!.AbsolutePath.EndsWith("/oauth/vk/token/standalone", StringComparison.Ordinal)
                    ? Json("""{"access_token":"sdk-token","refresh_token":"sdk-refresh","expires_in":2592000}""")
                    : Json("""{"data":{"channels":[{"channelUrl":"channel1","vkGroupId":7}]}}""");
            }));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        using var data = await sdk.SendAsync(HttpMethod.Get, "/v1/user/managed_channel/");

        // Web-токен мятится под app_id живого SDK, а не мессенджера — иначе обмен не примут.
        var mint = Assert.Single(mints);
        Assert.Equal("53729707", mint["app_id"]);

        var exchange = sdkCalls[0];
        Assert.Equal("/oauth/vk/token/standalone", exchange.Path);
        Assert.Equal("vk1.a.live-web-token", exchange.Form["access_token"]);
        Assert.Equal("53729707", exchange.Form["app_id"]);
        Assert.Equal("web", exchange.Form["device_os"]);
        Assert.False(string.IsNullOrWhiteSpace(exchange.Form["device_id"]));

        // Обмен идёт без Bearer — его ещё нет; сам вызов уже с ним.
        Assert.Null(exchange.Authorization);
        var call = sdkCalls[1];
        Assert.Equal("/v1/user/managed_channel/", call.Path);
        Assert.Equal("Bearer sdk-token", call.Authorization);
        Assert.Equal("streams_web", call.Headers["X-App"]);
        Assert.Equal("vkvideo_live_app", call.Headers["X-SDK-App"]);
        Assert.Equal(exchange.Form["device_id"], call.Headers["X-From-Id"]);

        // Токен сложен в сессию вместе со сроком, чтобы пережить перезапуск.
        Assert.Equal("sdk-token", session.LiveSdkToken);
        Assert.Equal("sdk-refresh", session.LiveSdkRefreshToken);
        Assert.Equal(exchange.Form["device_id"], session.LiveSdkDeviceId);
        Assert.True(session.LiveSdkTokenExpiresAt > DateTimeOffset.UtcNow.AddDays(29));

        // Токен сессии мессенджера не затронут: у него другой app_id.
        Assert.Equal("test-token", session.WebToken);

        // SendAsync разворачивает конверт "data".
        Assert.True(data.RootElement.TryGetProperty("channels", out _));
    }

    [Fact]
    public async Task Reuses_a_valid_token_without_minting_again()
    {
        var session = Session();
        session.LiveSdkToken = "stored-token";
        session.LiveSdkDeviceId = "stored-device";
        session.LiveSdkTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds();

        var sdkCalls = new List<SdkCall>();
        await using var client = Client(
            session,
            api: Handler((_, _) => throw new InvalidOperationException("web_token не должен запрашиваться.")),
            sdk: Handler(async (request, ct) =>
            {
                sdkCalls.Add(await SdkCall.FromAsync(request, ct));
                return Json("""{"data":{"ok":true}}""");
            }));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        using var _ = await sdk.SendAsync(HttpMethod.Get, "/v1/user/current");

        var call = Assert.Single(sdkCalls);
        Assert.Equal("Bearer stored-token", call.Authorization);
        Assert.Equal("stored-device", call.Headers["X-From-Id"]);
    }

    [Fact]
    public async Task Reissues_a_token_that_expires_within_the_skew()
    {
        var session = Session();
        session.LiveSdkToken = "almost-expired";
        session.LiveSdkTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var sdkCalls = new List<SdkCall>();
        await using var client = Client(
            session,
            api: Handler((_, _) => Task.FromResult(
                Json("""{"type":"okay","data":{"access_token":"vk1.a.fresh","expires":1}}"""))),
            sdk: Handler(async (request, ct) =>
            {
                sdkCalls.Add(await SdkCall.FromAsync(request, ct));
                return request.RequestUri!.AbsolutePath.EndsWith("standalone", StringComparison.Ordinal)
                    ? Json("""{"access_token":"reissued","expires_in":2592000}""")
                    : Json("""{"data":{}}""");
            }));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        using var _ = await sdk.SendAsync(HttpMethod.Get, "/v1/user/current");

        Assert.Equal("reissued", session.LiveSdkToken);
        Assert.Equal("Bearer reissued", sdkCalls[^1].Authorization);
    }

    [Fact]
    public async Task Reissues_once_when_the_token_is_rejected_and_retries_the_call()
    {
        var session = Session();
        session.LiveSdkToken = "revoked";
        session.LiveSdkTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds();

        var sdkCalls = new List<SdkCall>();
        await using var client = Client(
            session,
            api: Handler((_, _) => Task.FromResult(
                Json("""{"type":"okay","data":{"access_token":"vk1.a.fresh","expires":1}}"""))),
            sdk: Handler(async (request, ct) =>
            {
                var call = await SdkCall.FromAsync(request, ct);
                sdkCalls.Add(call);
                if (call.Path.EndsWith("standalone", StringComparison.Ordinal))
                    return Json("""{"access_token":"revived","expires_in":2592000}""");
                return call.Authorization == "Bearer revoked"
                    ? Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized)
                    : Json("""{"data":{"recovered":true}}""");
            }));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        using var data = await sdk.SendAsync(HttpMethod.Get, "/v1/user/current");

        Assert.True(data.RootElement.GetProperty("recovered").GetBoolean());
        Assert.Equal("revived", session.LiveSdkToken);
        Assert.Collection(
            sdkCalls.Select(c => c.Authorization),
            first => Assert.Equal("Bearer revoked", first),
            exchange => Assert.Null(exchange),
            retry => Assert.Equal("Bearer revived", retry));
    }

    [Fact]
    public async Task Gives_up_when_the_reissued_token_is_rejected_too()
    {
        var session = Session();
        session.LiveSdkToken = "revoked";
        session.LiveSdkTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds();

        await using var client = Client(
            session,
            api: Handler((_, _) => Task.FromResult(
                Json("""{"type":"okay","data":{"access_token":"vk1.a.fresh","expires":1}}"""))),
            sdk: Handler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("standalone", StringComparison.Ordinal)
                    ? Json("""{"access_token":"still-bad","expires_in":2592000}""")
                    : Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized))));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        await Assert.ThrowsAsync<VkSessionExpiredException>(
            () => sdk.SendAsync(HttpMethod.Get, "/v1/user/current"));
    }

    [Fact]
    public async Task Surfaces_a_readable_error_when_the_live_app_is_not_served()
    {
        await using var client = Client(
            Session(),
            api: Handler((_, _) => Task.FromResult(Json("""{"type":"error"}"""))),
            sdk: Handler((_, _) => throw new InvalidOperationException("До SDK дойти не должно.")));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<VkSessionExpiredException>(
            () => sdk.SendAsync(HttpMethod.Get, "/v1/user/current"));

        Assert.Contains("53729707", error.Message);
    }

    // --- обвязка -------------------------------------------------------------

    private static VkSession Session() => new()
    {
        UserId = 42,
        WebToken = "test-token",
        WebTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        Cookies =
        [
            new VkCookie { Name = "remixsid", Value = "secret", Domain = ".vk.ru", Secure = true, HttpOnly = true }
        ]
    };

    private static VkClient Client(VkSession session, HttpMessageHandler api, HttpMessageHandler sdk) =>
        new(new MemorySessionStore(session), new VkClientOptions
        {
            ApiHttpMessageHandlerFactory = () => api,
            LiveSdkHttpMessageHandlerFactory = () => sdk,
            UploadHttpMessageHandlerFactory = () => Handler(
                (_, _) => throw new InvalidOperationException("Upload не ожидался.")),
        });

    private static RecordingHandler Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(send);

    private static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static async Task<IReadOnlyDictionary<string, string>> Form(
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

    private sealed record SdkCall(
        string Path,
        IReadOnlyDictionary<string, string> Form,
        IReadOnlyDictionary<string, string> Headers,
        string? Authorization)
    {
        public static async Task<SdkCall> FromAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => new(
                request.RequestUri!.AbsolutePath,
                await VkLiveSdkApiTests.Form(request, cancellationToken),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                request.Headers.Authorization?.ToString());
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class MemorySessionStore(VkSession session) : ISessionStore
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
