using System.Net;
using System.Text;
using System.Text.Json;

namespace VkBrowserClient.Tests;

public sealed class VkSafeErrorDetailsTests
{
    private const string AccessToken = "vk1.synthetic-access-token-never-log";
    private const string Cookie = "synthetic-remixsid-never-log";
    private const string SignedUrl = "https://upload.example.test/file?signature=synthetic-signed-url-never-log";
    private const string StreamKey = "synthetic-stream-key-never-log";
    private const string Hash = "synthetic-provider-hash-never-log";

    [Fact]
    public void Json_description_exposes_only_bounded_structural_facts()
    {
        using var document = JsonDocument.Parse(SensitiveJson());

        var description = VkSafeErrorDetails.Describe(document.RootElement);

        Assert.Contains("type=rejected", description);
        Assert.Contains("code=15", description);
        Assert.Contains("upstream details redacted", description);
        Assert.InRange(description.Length, 1, VkSafeErrorDetails.MaxLength);
        AssertSafe(description);
    }

    [Fact]
    public void Plain_text_description_never_echoes_unstructured_payload()
    {
        var description = VkSafeErrorDetails.Describe(
            $"Authorization: Bearer {AccessToken}; cookie={Cookie}; upload={SignedUrl}");

        Assert.Equal("upstream details redacted", description);
        AssertSafe(description);
    }

    [Fact]
    public void Missing_response_envelope_never_echoes_provider_secrets()
    {
        using var document = JsonDocument.Parse(SensitiveJson());

        var error = Assert.Throws<VkClientException>(() =>
            VkWebApi.GetResponseOrThrow(document, "synthetic.method"));

        Assert.Contains("code=15", error.Message);
        AssertSafe(error.ToString());
    }

    [Fact]
    public async Task Upload_http_error_never_echoes_provider_secrets()
    {
        using var api = Api(
            api: new RecordingHandler((_, _) => throw new InvalidOperationException("API was not expected.")),
            uploads: new RecordingHandler((_, _) => Task.FromResult(Json(
                SensitiveJson(),
                HttpStatusCode.Forbidden))));

        var error = await Assert.ThrowsAsync<VkClientException>(() => api.UploadFileAsync(
            SignedUrl,
            "file",
            Encoding.UTF8.GetBytes("synthetic"),
            "sample.txt",
            "text/plain"));

        Assert.Contains("HTTP 403", error.Message);
        Assert.Contains("code=15", error.Message);
        AssertSafe(error.ToString());
    }

    [Fact]
    public async Task Upload_non_json_response_never_leaks_through_inner_exception()
    {
        using var api = Api(
            api: new RecordingHandler((_, _) => throw new InvalidOperationException("API was not expected.")),
            uploads: new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"token={AccessToken}; cookie={Cookie}; stream={StreamKey}; url={SignedUrl}",
                    Encoding.UTF8,
                    "text/plain")
            })));

        var error = await Assert.ThrowsAsync<VkClientException>(() => api.UploadFileAsync(
            SignedUrl,
            "file",
            Encoding.UTF8.GetBytes("synthetic"),
            "sample.txt",
            "text/plain"));

        Assert.Null(error.InnerException);
        AssertSafe(error.ToString());
    }

    [Fact]
    public async Task Rejected_web_token_response_never_echoes_authentication_data()
    {
        using var api = Api(
            api: new RecordingHandler((_, _) => Task.FromResult(Json(SensitiveJson()))),
            uploads: new RecordingHandler((_, _) => throw new InvalidOperationException("Upload was not expected.")));

        var error = await Assert.ThrowsAsync<VkSessionExpiredException>(() =>
            api.EnsureWebTokenAsync());

        Assert.Contains("type=rejected", error.Message);
        Assert.Contains("code=15", error.Message);
        AssertSafe(error.ToString());
    }

    [Fact]
    public async Task Non_json_web_token_response_never_leaks_through_inner_exception()
    {
        using var api = Api(
            api: new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"token={AccessToken}; cookie={Cookie}; stream={StreamKey}; url={SignedUrl}",
                    Encoding.UTF8,
                    "text/plain")
            })),
            uploads: new RecordingHandler((_, _) => throw new InvalidOperationException("Upload was not expected.")));

        var error = await Assert.ThrowsAsync<VkSessionExpiredException>(() =>
            api.EnsureWebTokenAsync());

        Assert.Null(error.InnerException);
        AssertSafe(error.ToString());
    }

    [Fact]
    public async Task Non_json_method_response_never_echoes_unstructured_payload()
    {
        var session = Session();
        using var api = Api(
            api: new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(
                    $"token={AccessToken}; cookie={Cookie}; stream={StreamKey}; url={SignedUrl}",
                    Encoding.UTF8,
                    "text/plain")
            })),
            uploads: new RecordingHandler((_, _) => throw new InvalidOperationException("Upload was not expected.")),
            session: session);

        var error = await Assert.ThrowsAsync<VkClientException>(() =>
            api.CallAsync("synthetic.method"));

        Assert.Contains("HTTP 502", error.Message);
        AssertSafe(error.ToString());
    }

    private static VkWebApi Api(
        HttpMessageHandler api,
        HttpMessageHandler uploads,
        VkSession? session = null) => new(session ?? new VkSession(), new VkClientOptions
        {
            ApiHttpMessageHandlerFactory = () => api,
            UploadHttpMessageHandlerFactory = () => uploads,
        });

    private static VkSession Session() => new()
    {
        WebToken = "synthetic-current-token",
        WebTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
    };

    private static string SensitiveJson() => $$"""
        {
          "type": "rejected",
          "error": {
            "error_code": 15,
            "error_msg": "failed at {{SignedUrl}} with {{AccessToken}}"
          },
          "access_token": "{{AccessToken}}",
          "cookies": { "remixsid": "{{Cookie}}" },
          "upload_url": "{{SignedUrl}}",
          "stream": { "key": "{{StreamKey}}", "url": "rtmp://stream.example.test/live/{{StreamKey}}" },
          "hash": "{{Hash}}"
        }
        """;

    private static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void AssertSafe(string value)
    {
        Assert.DoesNotContain(AccessToken, value);
        Assert.DoesNotContain(Cookie, value);
        Assert.DoesNotContain(SignedUrl, value);
        Assert.DoesNotContain(StreamKey, value);
        Assert.DoesNotContain(Hash, value);
        Assert.DoesNotContain("https://", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtmp://", value, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
