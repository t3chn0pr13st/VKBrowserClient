using System.Net;
using System.Text;
using System.Text.Json;

namespace VkBrowserClient.Tests;

public sealed class PublicationApiTests
{
    [Fact]
    public async Task Community_wall_streams_mixed_media_in_source_order()
    {
        var calls = new List<ApiCall>();
        var photoSaveId = 9;
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromAsync(request, cancellationToken);
            calls.Add(call);
            return call.Method switch
            {
                "photos.getWallUploadServer" => Json("""{"response":{"upload_url":"https://upload.test/photo"}}"""),
                "photos.saveWallPhoto" => Json($$"""{"response":[{"owner_id":-123,"id":{{++photoSaveId}}}]}"""),
                "video.save" => Json("""{"response":{"upload_url":"https://upload.test/video","owner_id":-123,"video_id":20}}"""),
                "wall.post" => Json("""{"response":{"post_id":77}}"""),
                _ => throw new InvalidOperationException($"Unexpected API method {call.Method}")
            };
        });
        var uploadFields = new List<string>();
        var uploads = new RecordingHandler(async (request, cancellationToken) =>
        {
            var body = Encoding.Latin1.GetString(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var field = body.Contains("video_file", StringComparison.Ordinal) ? "video_file" : "photo";
            uploadFields.Add(field);
            return field == "video_file"
                ? Json("""{"video_hash":"hash"}""")
                : Json("""{"server":1,"photo":"photo-token","hash":"hash"}""");
        });
        var photoOneOpens = 0;
        var videoOpens = 0;
        var photoTwoOpens = 0;
        var attachments = new[]
        {
            VkAttachmentSource.Photo(Source("one.jpg", "image/jpeg", 32_768, () => photoOneOpens++)),
            VkAttachmentSource.Video(Source("reel.mp4", "video/mp4", 64_000, () => videoOpens++), "Reel", "Description"),
            VkAttachmentSource.Photo(Source("two.jpg", "image/jpeg", 32_768, () => photoTwoOpens++)),
        };

        await using var client = Client(api, uploads);
        var result = await client.Wall.PostAsync("Caption", attachments, new VkWallPostOptions
        {
            CommunityId = 123,
            FromCommunity = true,
            IdempotencyKey = "job-0123456789"
        });

        Assert.Equal(-123, result.OwnerId);
        Assert.Equal(77, result.PostId);
        Assert.Equal("wall-123_77", result.Reference);
        Assert.Equal("https://vk.ru/wall-123_77", result.Url);
        Assert.Equal(["photo", "video_file", "photo"], uploadFields);
        Assert.Equal(1, photoOneOpens);
        Assert.Equal(1, videoOpens);
        Assert.Equal(1, photoTwoOpens);

        Assert.All(calls.Where(x => x.Method is "photos.getWallUploadServer" or "photos.saveWallPhoto"),
            x => Assert.Equal("123", x.Form["group_id"]));
        Assert.Equal("123", calls.Single(x => x.Method == "video.save").Form["group_id"]);
        var wall = calls.Single(x => x.Method == "wall.post").Form;
        Assert.Equal("-123", wall["owner_id"]);
        Assert.Equal("1", wall["from_group"]);
        Assert.Equal("Caption", wall["message"]);
        Assert.Equal("photo-123_10,video-123_20,photo-123_11", wall["attachments"]);
        Assert.Equal("job-0123456789", wall["guid"]);
    }

    [Fact]
    public async Task Transient_upload_failure_reopens_stream_before_retry()
    {
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromAsync(request, cancellationToken);
            return call.Method switch
            {
                "photos.getWallUploadServer" => Json("""{"response":{"upload_url":"https://upload.test/photo"}}"""),
                "photos.saveWallPhoto" => Json("""{"response":[{"owner_id":-123,"id":10}]}"""),
                "wall.post" => Json("""{"response":{"post_id":77}}"""),
                _ => throw new InvalidOperationException($"Unexpected API method {call.Method}")
            };
        });
        var uploadAttempts = 0;
        var uploads = new RecordingHandler(async (request, cancellationToken) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            uploadAttempts++;
            return uploadAttempts == 1
                ? Json("temporary", HttpStatusCode.MethodNotAllowed)
                : Json("""{"server":1,"photo":"photo-token","hash":"hash"}""");
        });
        var opens = 0;
        var attachment = VkAttachmentSource.Photo(Source("photo.jpg", "image/jpeg", 32_768, () => opens++));

        await using var client = Client(api, uploads);
        var result = await client.Wall.PostToCommunityAsync(123, "Caption", [attachment]);

        Assert.Equal(77, result.PostId);
        Assert.Equal(2, uploadAttempts);
        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task Clip_stream_publish_edit_and_processing_use_stable_provider_identity()
    {
        var calls = new List<ApiCall>();
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromAsync(request, cancellationToken);
            calls.Add(call);
            return call.Method switch
            {
                "shortVideo.create" => Json("""{"response":{"upload_url":"https://upload.test/clip","owner_id":-123,"video_id":42}}"""),
                "shortVideo.encodeProgress" => Json("""{"response":{"is_ready":true}}"""),
                "shortVideo.edit" => Json("""{"response":{"video":{"description":"updated"}}}"""),
                "shortVideo.publish" => Json("""{"response":1}"""),
                "video.get" => Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":42,"processing":0}]}}"""),
                _ => throw new InvalidOperationException($"Unexpected API method {call.Method}")
            };
        });
        var uploadOpens = 0;
        var uploads = new RecordingHandler(async (request, cancellationToken) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            return Json("""{"video_hash":"clip-hash"}""");
        });
        var source = Source("reel.mp4", "video/mp4", 64_000, () => uploadOpens++);

        await using var client = Client(api, uploads);
        var options = new VkClipPublishOptions
        {
            GroupId = 123,
            Description = "original",
            PostToWall = false
        };
        var created = await client.Clips.CreateUploadSessionAsync(source, options);
        var restoredCreated = JsonSerializer.Deserialize<VkClipUploadSession>(
            JsonSerializer.Serialize(created))!;
        var uploaded = await client.Clips.UploadAsync(restoredCreated, source);
        var restoredUploaded = JsonSerializer.Deserialize<VkClipUploadSession>(
            JsonSerializer.Serialize(uploaded))!;
        var clip = await client.Clips.CompletePublishAsync(restoredUploaded, options);
        var updated = await client.Clips.EditDescriptionAsync(clip, "updated");
        var processing = await client.Clips.GetProcessingStatusAsync(clip);

        Assert.Equal(VkClipUploadStage.Created, created.Stage);
        Assert.Equal(VkClipUploadStage.Uploaded, uploaded.Stage);
        Assert.Equal("clip-hash", uploaded.VideoHash);
        Assert.Equal(created.Reference, uploaded.Reference);
        Assert.Equal("video-123_42", clip.Reference);
        Assert.Equal("https://vk.ru/clip-123_42", clip.Url);
        Assert.Equal("updated", updated);
        Assert.Equal(VkVideoProcessingState.Ready, processing.State);
        Assert.Equal(1, uploadOpens);
        var create = calls.Single(x => x.Method == "shortVideo.create").Form;
        Assert.Equal("64000", create["file_size"]);
        Assert.Equal("123", create["group_id"]);
        Assert.Single(calls, x => x.Method == "shortVideo.create");
        var edits = calls.Where(x => x.Method == "shortVideo.edit").ToList();
        Assert.Equal("original", edits[0].Form["description"]);
        Assert.Equal("updated", edits[1].Form["description"]);
        Assert.Equal("-123_42", calls.Single(x => x.Method == "video.get").Form["videos"]);
    }

    [Fact]
    public async Task Wall_edit_updates_existing_post_without_reuploading_attachments()
    {
        ApiCall? edit = null;
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromAsync(request, cancellationToken);
            if (call.Method == "wall.getById")
            {
                return Json("""{"response":[{"id":77,"owner_id":-123,"attachments":[{"type":"photo","photo":{"owner_id":-123,"id":10}},{"type":"video","video":{"owner_id":-123,"id":20,"access_key":"key"}}]}]}""");
            }

            edit = call;
            return call.Method == "wall.edit"
                ? Json("""{"response":1}""")
                : throw new InvalidOperationException($"Unexpected API method {call.Method}");
        });
        var uploads = new RecordingHandler((_, _) => throw new InvalidOperationException("Upload was not expected."));

        await using var client = Client(api, uploads);
        var result = await client.Wall.EditTextAsync(-123, 77, "Updated caption");

        Assert.Equal("wall.edit", edit!.Method);
        Assert.Equal("-123", edit.Form["owner_id"]);
        Assert.Equal("77", edit.Form["post_id"]);
        Assert.Equal("Updated caption", edit.Form["message"]);
        Assert.Equal("photo-123_10,video-123_20_key", edit.Form["attachments"]);
        Assert.Equal("wall-123_77", result.Reference);
    }

    [Fact]
    public async Task Clip_upload_error_does_not_echo_provider_secrets()
    {
        const string accessToken = "vk1.synthetic-clip-token-never-log";
        const string signedUrl = "https://upload.test/clip?signature=synthetic-never-log";
        const string streamKey = "synthetic-clip-stream-key-never-log";
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromAsync(request, cancellationToken);
            return call.Method == "shortVideo.create"
                ? Json(JsonSerializer.Serialize(new
                {
                    response = new { upload_url = signedUrl, owner_id = -123, video_id = 42 }
                }))
                : throw new InvalidOperationException($"Unexpected API method {call.Method}");
        });
        var uploads = new RecordingHandler((_, _) => Task.FromResult(Json($$"""
            {
              "error": {
                "error_code": 17,
                "error_msg": "{{accessToken}} {{signedUrl}} {{streamKey}}"
              },
              "access_token": "{{accessToken}}",
              "upload_url": "{{signedUrl}}",
              "stream": { "key": "{{streamKey}}" }
            }
            """)));

        await using var client = Client(api, uploads);
        var source = Source("clip.mp4", "video/mp4", 64_000, () => { });
        var session = await client.Clips.CreateUploadSessionAsync(source, new VkClipPublishOptions());
        var error = await Assert.ThrowsAsync<VkClientException>(() =>
            client.Clips.UploadAsync(session, source));

        Assert.Contains("code=17", error.Message);
        Assert.DoesNotContain(accessToken, error.ToString());
        Assert.DoesNotContain(signedUrl, error.ToString());
        Assert.DoesNotContain(streamKey, error.ToString());
    }

    [Fact]
    public async Task Document_upload_error_does_not_echo_provider_secrets()
    {
        const string cookie = "synthetic-document-cookie-never-log";
        const string signedUrl = "https://upload.test/document?signature=synthetic-never-log";
        const string providerHash = "synthetic-document-hash-never-log";
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromAsync(request, cancellationToken);
            return call.Method == "docs.getWallUploadServer"
                ? Json(JsonSerializer.Serialize(new { response = new { upload_url = signedUrl } }))
                : throw new InvalidOperationException($"Unexpected API method {call.Method}");
        });
        var uploads = new RecordingHandler((_, _) => Task.FromResult(Json($$"""
            {
              "error": { "error_code": 18, "error_msg": "{{cookie}} {{signedUrl}} {{providerHash}}" },
              "cookies": { "remixsid": "{{cookie}}" },
              "upload_url": "{{signedUrl}}",
              "hash": "{{providerHash}}"
            }
            """)));

        await using var client = Client(api, uploads);
        var attachment = VkAttachmentSource.Document(
            Source("document.pdf", "application/pdf", 32_768, () => { }));
        var error = await Assert.ThrowsAsync<VkClientException>(() =>
            client.Wall.PostAsync("Caption", [attachment]));

        Assert.Contains("code=18", error.Message);
        Assert.DoesNotContain(cookie, error.ToString());
        Assert.DoesNotContain(signedUrl, error.ToString());
        Assert.DoesNotContain(providerHash, error.ToString());
    }

    private static VkClient Client(HttpMessageHandler api, HttpMessageHandler uploads)
    {
        var session = new VkSession
        {
            UserId = 42,
            WebToken = "test-token",
            WebTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            Cookies =
            [
                new VkCookie
                {
                    Name = "remixsid",
                    Value = "secret",
                    Domain = ".vk.ru",
                    Secure = true,
                    HttpOnly = true
                }
            ]
        };
        var options = new VkClientOptions
        {
            ApiHttpMessageHandlerFactory = () => api,
            UploadHttpMessageHandlerFactory = () => uploads
        };
        return new VkClient(new MemorySessionStore(session), options);
    }

    private static VkUploadSource Source(string fileName, string contentType, int length, Action opened)
    {
        var bytes = Enumerable.Range(0, length).Select(x => (byte)(x % 251)).ToArray();
        return VkUploadSource.Create(fileName, contentType, bytes.Length, _ =>
        {
            opened();
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        });
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

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

    private sealed record ApiCall(string Method, IReadOnlyDictionary<string, string> Form)
    {
        public static async Task<ApiCall> FromAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var method = Path.GetFileName(request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Split('=', 2))
                .ToDictionary(
                    item => Uri.UnescapeDataString(item[0].Replace('+', ' ')),
                    item => item.Length == 2
                        ? Uri.UnescapeDataString(item[1].Replace('+', ' '))
                        : "");
            return new ApiCall(method, form);
        }
    }
}
