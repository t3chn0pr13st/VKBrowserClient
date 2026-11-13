using System.Net;
using System.Text.Json;
using static VkBrowserClient.Tests.LiveSdkHarness;

namespace VkBrowserClient.Tests;

public sealed class VkVideoSessionRecoveryTests
{
    [Theory]
    [InlineData("remixdsid", -1, true)]
    [InlineData("remixsid", -1, true)]
    [InlineData("remixdsid", 1, false)]
    [InlineData("remixstid", -1, false)]
    [InlineData("remixdsid_other", -1, false)]
    public void Authentication_requires_exact_nonexpired_credential(string name, double expires, bool expected)
    {
        var cookie = new VkCookie { Name = name, Value = "credential", Domain = ".vkvideo.ru", Expires = expires };
        Assert.Equal(expected, VkCookieAuthentication.IsUsable(cookie, "vkvideo.ru", TimeSpan.FromMinutes(1)));
        Assert.False(VkCookieAuthentication.IsUsable(cookie, "evilvkvideo.ru", TimeSpan.Zero));
        Assert.False(VkCookieAuthentication.IsUsable(new() { Name = name, Value = "", Domain = ".vkvideo.ru" }, "vkvideo.ru", TimeSpan.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_failure_recovers_and_persists_exact_identity_before_command(bool expiredCookie)
    {
        var session = Session();
        session.Cookies.Add(new() { Name = "remixdsid", Value = "old", Domain = ".vkvideo.ru",
            Expires = expiredCookie ? 1 : DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() });
        var store = new Store(session);
        var topUp = new TopUp();
        var sequence = new List<string>();
        await using var client = Client(store, topUp, async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("al_video"))
            {
                sequence.Add("mint");
                return topUp.Calls == 0 ? new(HttpStatusCode.Found) : Json(WebTokenMinted);
            }
            var method = request.RequestUri.AbsolutePath.Split('/').Last();
            sequence.Add(method);
            if (method == "users.get") return Json("""{"response":[{"id":42}]}""");
            Assert.Equal("video.stopStreaming", method);
            Assert.True(store.RecoveredSaved); // durable recovery before sending the write
            await Task.CompletedTask;
            return Json("""{"response":{"unique_viewers":1}}""");
        });
        await client.Live.StopStreamingAsync(new() { OwnerId = -123, VideoId = 456 });
        Assert.Equal(1, topUp.Calls);
        Assert.Equal(1, sequence.Count(x => x == "video.stopStreaming"));
        Assert.Equal(1, sequence.Count(x => x == "users.get"));
        Assert.True(store.RecoveredSaved);
        Assert.Equal(42, session.UserId);
        Assert.Contains(session.Cookies, c => c.Name == "remixdsid" && c.Value == "fresh");
    }

    [Theory]
    [InlineData("no_cookie")]
    [InlineData("different_user")]
    [InlineData("probe_mismatch")]
    [InlineData("returns_false")]
    [InlineData("browser_missing")]
    [InlineData("probe_denied")]
    public async Task Failed_recovery_never_commits_candidate_or_sends_command(string failure)
    {
        var session = Session();
        var store = new Store(session);
        var topUp = new TopUp(failure);
        var commands = 0;
        await using var client = Client(store, topUp, (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("al_video"))
                return Task.FromResult(topUp.Calls == 0 || failure == "probe_denied"
                    ? new HttpResponseMessage(HttpStatusCode.Found) : Json(WebTokenMinted));
            if (request.RequestUri.AbsolutePath.EndsWith("users.get"))
                return Task.FromResult(Json(failure == "probe_mismatch"
                    ? """{"response":[{"id":99}]}""" : """{"response":[{"id":42}]}"""));
            commands++;
            throw new InvalidOperationException("Command must not be sent");
        });
        var error = await Record.ExceptionAsync(() => client.Live.StopStreamingAsync(new() { OwnerId = -123, VideoId = 456 }));
        Assert.True(error is VkSessionExpiredException or VkAuthenticationException, error?.ToString());
        Assert.DoesNotContain("sensitive-browser-url", error!.Message);
        Assert.Equal(1, topUp.Calls);
        Assert.Equal(0, commands);
        Assert.False(store.RecoveredSaved);
        Assert.DoesNotContain(session.Cookies, c => c.Value == "fresh");
        Assert.Equal(42, session.UserId);
    }

    [Fact]
    public async Task Transport_failure_after_command_is_never_replayed()
    {
        var store = new Store(Session());
        var topUp = new TopUp();
        var commands = 0;
        await using var client = Client(store, topUp, (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("al_video"))
                return Task.FromResult(topUp.Calls == 0 ? new(HttpStatusCode.Found) : Json(WebTokenMinted));
            if (request.RequestUri.AbsolutePath.EndsWith("users.get")) return Task.FromResult(Json("""{"response":[{"id":42}]}"""));
            commands++;
            throw new HttpRequestException("Lost ACK");
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.Live.StopStreamingAsync(new() { OwnerId = -123, VideoId = 456 }));
        Assert.Equal(1, commands);
        Assert.Equal(1, topUp.Calls);
    }

    [Fact]
    public async Task Dead_main_session_does_not_attempt_video_recovery()
    {
        var session = Session(); session.WebToken = null;
        var topUp = new TopUp();
        await using var client = Client(new Store(session), topUp, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)));
        await Assert.ThrowsAsync<VkAuthenticationException>(() => client.EnsureVideoAuthenticatedAsync());
        Assert.Equal(0, topUp.Calls);
    }

    [Fact]
    public async Task Live_sdk_token_mint_uses_the_same_recovery_path()
    {
        var session = Session(); var store = new Store(session); var topUp = new TopUp();
        var options = Options(topUp, (request, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.Contains("al_video")
                ? topUp.Calls == 0 ? new(HttpStatusCode.Found) : Json(WebTokenMinted)
                : Json("""{"response":[{"id":42}]}""")));
        options.LiveSdkHttpMessageHandlerFactory = () => Handler((request, _) => Task.FromResult(
            IsTokenExchange(request) ? Json(SdkTokenIssued) : Json("""{"data":{"streamSlot":{"slotUrl":"slot","title":"t","vkPermission":"public","isShouldRecord":true}}}""")));
        await using var client = new VkClient(store, options);
        await client.LiveSdk.GetStreamPermissionAsync("channel", "slot");
        Assert.Equal(1, topUp.Calls);
        Assert.True(store.RecoveredSaved);
    }

    [Fact]
    public async Task Healthy_video_session_does_not_launch_browser()
    {
        var store = new Store(Session()); var topUp = new TopUp();
        await using var client = Client(store, topUp, (request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("al_video") ? Json(WebTokenMinted) : Json("""{"response":[{"id":42}]}""")));
        await client.EnsureVideoAuthenticatedAsync();
        Assert.Equal(0, topUp.Calls);
    }

    [Fact]
    public async Task Recovery_cancellation_propagates_without_sending_command_or_saving_candidate()
    {
        var session = Session(); var store = new Store(session); var topUp = new TopUp("cancelled");
        using var cancellation = new CancellationTokenSource();
        topUp.Cancel = cancellation.Cancel;
        await using var client = Client(store, topUp, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EnsureVideoAuthenticatedAsync(cancellation.Token));
        Assert.Equal(1, topUp.Calls);
        Assert.False(store.RecoveredSaved);
    }

    private static VkClient Client(Store store, TopUp topUp, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(store, Options(topUp, send));
    private static VkClientOptions Options(TopUp topUp, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new()
    {
        AuthenticatorFactory = _ => new NeverInteractiveAuthenticator(),
        SessionCookieTopUpFactory = _ => topUp,
        ApiHttpMessageHandlerFactory = () => Handler(send),
    };
    private sealed class Store(VkSession session) : ISessionStore
    {
        public bool RecoveredSaved { get; private set; }
        public Task<VkSession?> LoadAsync(CancellationToken ct = default) => Task.FromResult<VkSession?>(session);
        public Task SaveAsync(VkSession value, CancellationToken ct = default)
        {
            RecoveredSaved |= value.Cookies.Any(c => c.Value == "fresh");
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class TopUp(string failure = "") : ISessionCookieTopUp
    {
        public int Calls { get; private set; }
        public Action? Cancel { get; set; }
        public Task<bool> TopUpAsync(VkSession candidate, string host, CancellationToken ct = default)
        {
            Calls++;
            Assert.Equal("vkvideo.ru", host);
            Cancel?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (failure == "browser_missing") throw new InvalidOperationException("sensitive-browser-url");
            if (failure == "different_user") candidate.UserId = 99;
            candidate.Cookies.RemoveAll(c => c.Domain == ".vkvideo.ru");
            if (failure != "no_cookie") candidate.Cookies.Add(new() { Name = "remixdsid", Value = "fresh", Domain = ".vkvideo.ru" });
            return Task.FromResult(failure != "returns_false");
        }
    }
}
