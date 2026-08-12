using System.Net;

using static VkBrowserClient.Tests.LiveSdkHarness;

namespace VkBrowserClient.Tests;

public sealed class VkLiveSdkApiTests
{
    [Fact]
    public async Task Issues_sdk_token_through_the_live_app_and_persists_it()
    {
        var mints = new List<(Uri Url, IReadOnlyDictionary<string, string> Form)>();
        var sdkCalls = new List<SdkCall>();
        var session = Session();

        await using var client = Client(
            session,
            sdk: Handler(async (request, ct) =>
            {
                sdkCalls.Add(await SdkCall.FromAsync(request, ct));
                return IsTokenExchange(request)
                    ? Json(SdkTokenIssued)
                    : Json("""{"data":{"channels":[{"channelUrl":"channel1","vkGroupId":7}]}}""");
            }),
            api: Handler(async (request, ct) =>
            {
                mints.Add((request.RequestUri!, await FormAsync(request, ct)));
                return Json(WebTokenMinted);
            }));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        using var data = await sdk.SendAsync(HttpMethod.Get, "/v1/user/managed_channel/");

        // Токен приложения live-SDK выпускается на vkvideo.ru: login.vk.ru на этот app_id
        // отвечает ошибкой, хотя мессенджеру в той же сессии токен выдаёт.
        var mint = Assert.Single(mints);
        Assert.Equal("vkvideo.ru", mint.Url.Host);
        Assert.Equal("53729707", mint.Form["app_id"]);
        // Запрос повторяет страницу: уже выпущенный web-токен едет в теле.
        Assert.Equal("test-token", mint.Form["access_token"]);

        var exchange = sdkCalls[0];
        Assert.Equal("/oauth/vk/token/standalone", exchange.Path);
        Assert.Equal("vk1.a.live-web-token", exchange.Form["access_token"]);
        Assert.Equal("53729707", exchange.Form["app_id"]);
        Assert.Equal("web", exchange.Form["device_os"]);
        Assert.False(string.IsNullOrWhiteSpace(exchange.Form["device_id"]));

        // Сам обмен идёт без Bearer — его ещё нет; последующий вызов уже с ним.
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
        var session = SessionWithSdkToken("stored-token");
        var sdkCalls = new List<SdkCall>();

        await using var client = Client(
            session,
            sdk: Handler(async (request, ct) =>
            {
                sdkCalls.Add(await SdkCall.FromAsync(request, ct));
                return Json("""{"data":{"ok":true}}""");
            }),
            api: Handler((_, _) => throw new InvalidOperationException("web_token не должен запрашиваться.")));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        using var _ = await sdk.SendAsync(HttpMethod.Get, "/v1/user/current");

        var call = Assert.Single(sdkCalls);
        Assert.Equal("Bearer stored-token", call.Authorization);
        Assert.Equal("test-device", call.Headers["X-From-Id"]);
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
            sdk: Handler(async (request, ct) =>
            {
                sdkCalls.Add(await SdkCall.FromAsync(request, ct));
                return IsTokenExchange(request)
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
        var session = SessionWithSdkToken("revoked");
        var sdkCalls = new List<SdkCall>();

        await using var client = Client(
            session,
            sdk: Handler(async (request, ct) =>
            {
                var call = await SdkCall.FromAsync(request, ct);
                sdkCalls.Add(call);
                if (IsTokenExchange(request))
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
        await using var client = Client(
            SessionWithSdkToken("revoked"),
            sdk: Handler((request, _) => Task.FromResult(
                IsTokenExchange(request)
                    ? Json("""{"access_token":"still-bad","expires_in":2592000}""")
                    : Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized))));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        await Assert.ThrowsAsync<VkSessionExpiredException>(
            () => sdk.SendAsync(HttpMethod.Get, "/v1/user/current"));
    }

    [Fact]
    public async Task Points_at_a_missing_cookie_domain_rather_than_at_an_expired_session()
    {
        // Сессии, снятые до появления live-SDK, содержат cookie только для vk.ru,
        // и vkvideo.ru отвечает им редиректом на вход.
        await using var client = Client(
            Session(),
            sdk: Handler((_, _) => throw new InvalidOperationException("До SDK дойти не должно.")),
            api: Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found))));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<VkSessionExpiredException>(
            () => sdk.SendAsync(HttpMethod.Get, "/v1/user/current"));

        Assert.Contains("нет cookie для vkvideo.ru", error.Message);
        Assert.DoesNotContain("истекла", error.Message);
    }

    [Fact]
    public async Task Surfaces_a_readable_error_when_the_live_app_is_not_served()
    {
        await using var client = Client(
            Session(),
            sdk: Handler((_, _) => throw new InvalidOperationException("До SDK дойти не должно.")),
            api: Handler((_, _) => Task.FromResult(Json("""{"type":"error"}"""))));

        var sdk = await client.RequireLiveSdkApiAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<VkSessionExpiredException>(
            () => sdk.SendAsync(HttpMethod.Get, "/v1/user/current"));

        Assert.Contains("53729707", error.Message);
    }
}
