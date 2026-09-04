using System.Net;
using System.Text;
using System.Text.Json;

namespace VkBrowserClient.Tests;

public sealed class VkLiveServiceTests
{
    [Fact]
    public async Task Start_streaming_maps_official_contract_and_returns_stable_anchor()
    {
        ApiCall? call = null;
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            call = await ApiCall.FromAsync(request, cancellationToken);
            return Json("""
                {"response":{
                  "owner_id":-123,"video_id":456,"name":"Provider title","description":"Provider description",
                  "access_key":"private-access","post_id":77,
                  "stream":{"url":"rtmps://ingest.vk.test/live","key":"stream-secret","okmp_url":"okmp://ingest","webrtc_url":"https://webrtc.test"}
                }}
                """);
        });

        await using var client = Client(api);
        Assert.Same(client.Live, client.Live);
        var stream = await client.Live.StartStreamingAsync(new VkLiveStartOptions
        {
            Name = "Requested title",
            Description = "Requested description",
            GroupId = 123,
            ViewPrivacy = VkLivePrivacy.OnlyMe,
            CommentPrivacy = VkLivePrivacy.Friends,
            DisableComments = true,
            CategoryId = 9,
            Publish = false,
            PostToWall = false,
        });

        Assert.Equal("video.startStreaming", call!.Method);
        Assert.Equal("Requested title", call.Form["name"]);
        Assert.Equal("Requested description", call.Form["description"]);
        Assert.Equal("123", call.Form["group_id"]);
        Assert.Equal("only_me", call.Form["privacy_view"]);
        Assert.Equal("friends", call.Form["privacy_comment"]);
        Assert.Equal("1", call.Form["no_comments"]);
        Assert.Equal("9", call.Form["category_id"]);
        Assert.Equal("0", call.Form["publish"]);
        Assert.Equal("0", call.Form["wallpost"]);
        Assert.DoesNotContain("video_id", call.Form.Keys);

        Assert.Equal(-123, stream.OwnerId);
        Assert.Equal(456, stream.VideoId);
        Assert.Equal("Provider title", stream.Name);
        Assert.Equal("Provider description", stream.Description);
        Assert.Equal("private-access", stream.AccessKey);
        Assert.Equal("rtmps://ingest.vk.test/live", stream.Ingest.Url);
        Assert.Equal("stream-secret", stream.Ingest.Key);
        Assert.Equal("okmp://ingest", stream.Ingest.OkmpUrl);
        Assert.Equal("https://webrtc.test", stream.Ingest.WebRtcUrl);
        Assert.Equal(77, stream.PostId);
        Assert.Equal("video-123_456", stream.Reference);
        Assert.Equal("https://vk.ru/video-123_456", stream.Url);

        var reference = stream.ToReference();
        Assert.Equal("private-access", reference.AccessKey);
        Assert.DoesNotContain("private-access", reference.Reference);
        Assert.DoesNotContain("private-access", reference.Url);
        Assert.DoesNotContain("private-access", reference.ToString());
        Assert.DoesNotContain("private-access", stream.ToString());
        Assert.DoesNotContain("stream-secret", stream.ToString());
        Assert.DoesNotContain("stream-secret", stream.Ingest.ToString());
    }

    [Fact]
    public async Task Start_streaming_with_existing_video_addresses_same_provider_id()
    {
        ApiCall? call = null;
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            call = await ApiCall.FromAsync(request, cancellationToken);
            return Json("""{"response":{"owner_id":42,"video_id":456,"name":"Live","description":"","access_key":"","stream":{"url":"rtmp://ingest","key":"key"}}}""");
        });

        await using var client = Client(api);
        var stream = await client.Live.StartStreamingAsync(new VkLiveStartOptions
        {
            VideoId = 456,
            Publish = true,
            PostToWall = true,
        });

        Assert.Equal(456, stream.VideoId);
        Assert.Equal("456", call!.Form["video_id"]);
        Assert.Equal("1", call.Form["publish"]);
        Assert.Equal("1", call.Form["wallpost"]);
        Assert.Equal("all", call.Form["privacy_view"]);
        Assert.Equal("all", call.Form["privacy_comment"]);
        Assert.DoesNotContain("group_id", call.Form.Keys);
        Assert.DoesNotContain("name", call.Form.Keys);
        Assert.DoesNotContain("description", call.Form.Keys);
    }

    [Fact]
    public async Task Malformed_start_response_does_not_echo_stream_key()
    {
        var api = new RecordingHandler((_, _) => Task.FromResult(Json("""
            {"response":{"owner_id":0,"video_id":456,"access_key":"access-secret","stream":{"url":"rtmp://ingest","key":"stream-secret"}}}
            """)));

        await using var client = Client(api);
        var error = await Assert.ThrowsAsync<VkClientException>(() =>
            client.Live.StartStreamingAsync(new VkLiveStartOptions()));

        Assert.DoesNotContain("stream-secret", error.Message);
        Assert.DoesNotContain("access-secret", error.Message);
        Assert.Contains("owner_id/video_id/stream", error.Message);
    }

    [Fact]
    public async Task Missing_response_envelope_never_echoes_live_secrets()
    {
        var api = new RecordingHandler((_, _) => Task.FromResult(Json("""
            {"unexpected":{"access_key":"access-secret","stream":{"key":"stream-secret"}}}
            """)));

        await using var client = Client(api);
        var error = await Assert.ThrowsAsync<VkClientException>(() =>
            client.Live.StartStreamingAsync(new VkLiveStartOptions()));

        Assert.DoesNotContain("stream-secret", error.Message);
        Assert.DoesNotContain("access-secret", error.Message);
        Assert.Contains("не содержит поля 'response'", error.Message);
    }

    [Fact]
    public async Task Categories_are_parsed_recursively()
    {
        ApiCall? call = null;
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            call = await ApiCall.FromAsync(request, cancellationToken);
            return Json("""
                {"response":[
                  {"id":1,"label":"Education","sublist":[{"id":11,"label":"Yoga"}]},
                  {"id":2,"label":"Other"}
                ]}
                """);
        });

        await using var client = Client(api);
        var categories = await client.Live.GetCategoriesAsync();

        Assert.Equal("video.liveGetCategories", call!.Method);
        Assert.Equal(2, categories.Count);
        Assert.Equal("Education", categories[0].Label);
        Assert.Single(categories[0].Children);
        Assert.Equal(11, categories[0].Children[0].Id);
        Assert.Empty(categories[1].Children);
    }

    [Fact]
    public async Task Thumbnail_flow_has_durable_stages_reopens_source_and_sets_video_thumb()
    {
        var calls = new List<ApiCall>();
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromAsync(request, cancellationToken);
            calls.Add(call);
            return call.Method switch
            {
                "video.getThumbUploadUrl" => Json("""{"response":{"upload_url":"https://upload.vk.test/thumb?sig=secret"}}"""),
                "video.saveUploadedThumb" => Json("""
                    {"response":{"photo_id":987,"photo_owner_id":-123,"photo_hash":"provider-hash",
                    "image":[{"url":"https://cdn.vk.test/cover.jpg","width":1920,"height":1080,"size":"w"}]}}
                    """),
                _ => throw new InvalidOperationException($"Unexpected API method {call.Method}")
            };
        });
        var uploadAttempts = 0;
        var multipartBodies = new List<string>();
        var uploads = new RecordingHandler(async (request, cancellationToken) =>
        {
            multipartBodies.Add(Encoding.Latin1.GetString(
                await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            uploadAttempts++;
            return uploadAttempts == 1
                ? Json("temporary", HttpStatusCode.ServiceUnavailable)
                : Json("""{"upload_id":321,"thumb_size":"1920x1080","random_tag":"tag-1"}""");
        });
        var opens = 0;
        var source = Source("cover.jpg", "image/jpeg", 32_768, () => opens++);

        await using var client = Client(api, uploads);
        var session = await client.Live.CreateThumbnailUploadSessionAsync(-123, 456);
        session = JsonSerializer.Deserialize<VkLiveThumbnailUploadSession>(
            JsonSerializer.Serialize(session))!;
        var uploaded = await client.Live.UploadThumbnailAsync(session, source);
        uploaded = JsonSerializer.Deserialize<VkLiveThumbnailUpload>(JsonSerializer.Serialize(uploaded))!;
        var saved = await client.Live.SaveThumbnailAsync(uploaded);

        Assert.Equal(2, uploadAttempts);
        Assert.Equal(2, opens);
        Assert.All(multipartBodies, body =>
        {
            Assert.Contains("name=file", body);
            Assert.Contains("filename=cover.jpg", body);
        });
        Assert.Equal("{\"upload_id\":321,\"thumb_size\":\"1920x1080\",\"random_tag\":\"tag-1\"}", uploaded.ThumbJson);
        Assert.Equal("1920x1080", uploaded.ThumbSize);
        Assert.Equal("tag-1", uploaded.RandomTag);
        Assert.DoesNotContain("sig=secret", session.ToString());
        Assert.DoesNotContain("upload_id", uploaded.ToString());
        Assert.DoesNotContain("tag-1", uploaded.ToString());

        var getUrl = calls.Single(x => x.Method == "video.getThumbUploadUrl").Form;
        Assert.Equal("-123", getUrl["owner_id"]);
        var save = calls.Single(x => x.Method == "video.saveUploadedThumb").Form;
        Assert.Equal("-123", save["owner_id"]);
        Assert.Equal("456", save["video_id"]);
        Assert.Equal("{\"upload_id\":321,\"thumb_size\":\"1920x1080\",\"random_tag\":\"tag-1\"}", save["thumb_json"]);
        Assert.Equal("1920x1080", save["thumb_size"]);
        Assert.Equal("tag-1", save["random_tag"]);
        Assert.Equal("1", save["set_thumb"]);

        Assert.Equal(987, saved.PhotoId);
        Assert.Equal(-123, saved.PhotoOwnerId);
        Assert.Equal("provider-hash", saved.PhotoHash);
        Assert.Single(saved.Images);
        Assert.Equal(1920, saved.Images[0].Width);
    }

    [Fact]
    public async Task Update_sends_only_requested_fields_and_returns_rotated_access_key()
    {
        ApiCall? call = null;
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            call = await ApiCall.FromAsync(request, cancellationToken);
            return Json("""{"response":{"success":1,"access_key":"rotated-key"}}""");
        });

        await using var client = Client(api);
        var result = await client.Live.UpdateAsync(-123, 456, new VkLiveUpdateOptions
        {
            Description = "Updated",
            ViewPrivacy = VkLivePrivacy.OnlyMe,
            DisableComments = false,
        });

        Assert.True(result.Success);
        Assert.Equal("rotated-key", result.AccessKey);
        Assert.Equal("video.edit", call!.Method);
        Assert.Equal("-123", call.Form["owner_id"]);
        Assert.Equal("456", call.Form["video_id"]);
        Assert.Equal("Updated", call.Form["desc"]);
        Assert.Equal("only_me", call.Form["privacy_view"]);
        Assert.Equal("0", call.Form["no_comments"]);
        Assert.DoesNotContain("name", call.Form.Keys);
        Assert.DoesNotContain("privacy_comment", call.Form.Keys);
        Assert.DoesNotContain("repeat", call.Form.Keys);
    }

    [Fact]
    public async Task Status_uses_private_api_reference_and_normalizes_lifecycle_states()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":456,"type":"live","upcoming":1,"live":1,"title":"Soon","live_start_time":1893456000}]}}"""),
            Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":456,"type":"live","live":1,"spectators":17,"views":42,"player":"https://vk.test/player"}]}}"""),
            Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":456,"type":"live","processing":1}]}}"""),
            Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":456,"type":"live","is_private":1,"can_edit":1,"can_delete":true,"access_key":"new-key","image":[{"url":"https://cdn/cover.jpg","width":1280,"height":720}] }]}}"""),
        ]);
        var calls = new List<ApiCall>();
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            calls.Add(await ApiCall.FromAsync(request, cancellationToken));
            return responses.Dequeue();
        });

        await using var client = Client(api);
        var upcoming = await client.Live.GetStatusAsync(-123, 456, "private-key");
        var live = await client.Live.GetStatusAsync(-123, 456, "private-key");
        var processing = await client.Live.GetStatusAsync(-123, 456, "private-key");
        var ready = await client.Live.GetStatusAsync(-123, 456, "private-key");

        Assert.Equal(VkLiveStatusState.Upcoming, upcoming.State);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), upcoming.ScheduledStartAt);
        Assert.Equal(VkLiveStatusState.Live, live.State);
        Assert.Equal(17, live.Spectators);
        Assert.Equal(17, live.CurrentViewers);
        Assert.Equal(42, live.TotalViews);
        Assert.Equal("https://vk.test/player", live.PlayerUrl);
        Assert.Equal(VkLiveStatusState.Processing, processing.State);
        Assert.Equal(VkLiveStatusState.Ready, ready.State);
        Assert.True(ready.IsPrivate);
        Assert.True(ready.PrivacyKnown);
        Assert.True(ready.CanEdit);
        Assert.True(ready.CanDelete);
        Assert.Equal("new-key", ready.AccessKey);
        Assert.Single(ready.Images);
        Assert.All(calls, call =>
        {
            Assert.Equal("video.get", call.Method);
            Assert.Equal("-123_456_private-key", call.Form["videos"]);
            Assert.Equal("1", call.Form["count"]);
        });
    }

    [Fact]
    public async Task Status_distinguishes_missing_audience_counters_from_confirmed_zero()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":456,"type":"live","live":1}]}}"""),
            Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":456,"type":"live","live":1,"spectators":0,"views":0}]}}"""),
        ]);
        var api = new RecordingHandler((_, _) => Task.FromResult(responses.Dequeue()));
        await using var client = Client(api);

        var missing = await client.Live.GetStatusAsync(-123, 456, cancellationToken: default);
        var zero = await client.Live.GetStatusAsync(-123, 456, cancellationToken: default);

        Assert.Null(missing.CurrentViewers);
        Assert.Null(missing.TotalViews);
        Assert.Equal(0, missing.Spectators);
        Assert.Equal(0, zero.CurrentViewers);
        Assert.Equal(0, zero.TotalViews);
    }

    [Fact]
    public async Task Empty_video_get_is_not_found_without_losing_anchor()
    {
        var api = new RecordingHandler((_, _) => Task.FromResult(
            Json("""{"response":{"count":0,"items":[]}}""")));

        await using var client = Client(api);
        var status = await client.Live.GetStatusAsync(-123, 456, "access");

        Assert.Equal(VkLiveStatusState.NotFound, status.State);
        Assert.Equal(-123, status.OwnerId);
        Assert.Equal(456, status.VideoId);
        Assert.Equal("access", status.AccessKey);
    }

    [Fact]
    public async Task Status_extracts_the_ln_grant_from_vk_live_video_id()
    {
        var api = new RecordingHandler((_, _) => Task.FromResult(Json(
            """{"response":{"count":1,"items":[{"owner_id":-123,"id":456,"type":"live","upcoming":1,"vk_live_video_id":"-123_456_ln-synthetic_Grant-01"}]}}""")));
        await using var client = Client(api);

        var status = await client.Live.GetStatusAsync(-123, 456, cancellationToken: default);

        Assert.Equal("ln-synthetic_Grant-01", status.AccessKey);
        Assert.False(status.PrivacyKnown);
    }

    [Fact]
    public async Task Community_stop_and_delete_map_owner_semantics()
    {
        var calls = new List<(string Url, ApiCall Call)>();
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var url = request.RequestUri!.ToString();
            var call = await ApiCall.FromAsync(request, cancellationToken);
            calls.Add((url, call));
            if (url.Contains("act=web_token", StringComparison.Ordinal))
                return Json("""{"type":"okay","data":{"access_token":"video-app-token","expires":1800,"user_id":42}}""");
            return call.Method switch
            {
                "video.stopStreaming" => Json("""{"response":{"unique_viewers":321}}"""),
                "video.delete" => Json("""{"response":1}"""),
                _ => throw new InvalidOperationException($"Unexpected {call.Method}")
            };
        });

        await using var client = Client(api);
        var stopped = await client.Live.StopStreamingAsync(-123, 456);
        var deleted = await client.Live.DeleteAsync(-123, 456);

        Assert.Equal(321, stopped.UniqueViewers);
        Assert.True(deleted);
        var stopRequest = calls.Single(x => x.Call.Method == "video.stopStreaming");
        Assert.StartsWith("https://api.vkvideo.ru/method/video.stopStreaming?", stopRequest.Url, StringComparison.Ordinal);
        var stop = stopRequest.Call.Form;
        Assert.Equal("123", stop["group_id"]);
        Assert.Equal("456", stop["video_id"]);
        Assert.Equal("0", stop["extended"]);
        Assert.Equal("video-app-token", stop["access_token"]);
        Assert.DoesNotContain("owner_id", stop.Keys);
        var delete = calls.Single(x => x.Call.Method == "video.delete").Call.Form;
        Assert.Equal("-123", delete["owner_id"]);
        Assert.Equal("456", delete["video_id"]);
    }

    [Fact]
    public async Task Api_error_preserves_typed_method_and_code()
    {
        var api = new RecordingHandler((_, _) => Task.FromResult(
            Json("""{"error":{"error_code":15,"error_msg":"Access denied"}}""")));

        await using var client = Client(api);
        var error = await Assert.ThrowsAsync<VkApiException>(() =>
            client.Live.GetCategoriesAsync());

        Assert.Equal("video.liveGetCategories", error.Method);
        Assert.Equal(15, error.ErrorCode);
    }

    [Fact]
    public async Task Invalid_options_fail_before_provider_call()
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json("""{"response":1}"""));
        });

        await using var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.Live.StartStreamingAsync(new VkLiveStartOptions { GroupId = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.Live.UpdateAsync(42, 1, new VkLiveUpdateOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.Live.SetThumbnailAsync(42, 1, Source("file.txt", "text/plain", 10, () => { })));

        Assert.Equal(0, calls);
    }

    [Theory]
    // live=1 значит «объект — трансляция», а не «идёт сейчас»: фазу несёт live_status.
    [InlineData("started", VkLiveStatusState.Live)]
    [InlineData("waiting", VkLiveStatusState.Upcoming)]
    [InlineData("finished", VkLiveStatusState.Ready)]
    [InlineData("postlive", VkLiveStatusState.Ready)]
    // Новая фаза VK не должна снова читаться как «идёт сейчас».
    [InlineData("something_new", VkLiveStatusState.Ready)]
    [InlineData("failed", VkLiveStatusState.Unknown)]
    public async Task Live_phase_is_read_from_live_status_not_from_the_live_flag(
        string liveStatus,
        VkLiveStatusState expected)
    {
        var api = new RecordingHandler((_, _) => Task.FromResult(Json(
            "{\"response\":{\"count\":1,\"items\":[{\"owner_id\":-123,\"id\":456,\"type\":\"live\",\"live\":1,"
            + $"\"live_status\":\"{liveStatus}\"}}]}}}}")));
        await using var client = Client(api);

        var status = await client.Live.GetStatusAsync(-123, 456);

        Assert.Equal(expected, status.State);
        Assert.Equal(liveStatus, status.ProviderStatus);
    }

    [Fact]
    public async Task A_response_without_live_status_keeps_the_previous_reading()
    {
        var api = new RecordingHandler((_, _) => Task.FromResult(Json(
            """{"response":{"count":1,"items":[{"owner_id":-123,"id":456,"type":"live","live":1}]}}""")));
        await using var client = Client(api);

        var status = await client.Live.GetStatusAsync(-123, 456);

        Assert.Equal(VkLiveStatusState.Live, status.State);
        Assert.Null(status.ProviderStatus);
    }

    [Fact]
    public async Task Video_privacy_goes_to_the_vk_video_app_on_its_own_host()
    {
        var requests = new List<(string Url, IReadOnlyDictionary<string, string> Form)>();
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var url = request.RequestUri!.ToString();
            var call = await ApiCall.FromAsync(request, cancellationToken);
            requests.Add((url, call.Form));
            if (url.Contains("act=web_token", StringComparison.Ordinal))
                return Json("""{"type":"okay","data":{"access_token":"video-app-token","expires":1800,"user_id":42}}""");
            if (url.Contains("video.edit", StringComparison.Ordinal))
                return Json("""{"response":{"success":1}}""");
            return Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":456,"privacy_view":"by_link"}]}}""");
        });

        await using var client = Client(api);
        var result = await client.Live.SetVideoPrivacyAsync(
            -123, 456, VkLivePrivacy.ByLink, name: "Йога-пикник");

        Assert.True(result.Accepted);
        Assert.Equal("by_link", result.Privacy);
        Assert.True(result.Confirms(VkLivePrivacy.ByLink));

        // Токен выпускается под приложением VK Видео на его сайте.
        var mint = requests[0];
        Assert.Contains("vkvideo.ru/al_video.php?act=web_token", mint.Url, StringComparison.Ordinal);
        Assert.Equal("52461373", mint.Form["app_id"]);

        // Правка уходит на хост VK Видео с его client_id и версией.
        var edit = requests[1];
        Assert.StartsWith("https://api.vkvideo.ru/method/video.edit?", edit.Url, StringComparison.Ordinal);
        Assert.Contains("client_id=52461373", edit.Url, StringComparison.Ordinal);
        Assert.Contains("v=5.285", edit.Url, StringComparison.Ordinal);
        Assert.Equal("-123", edit.Form["owner_id"]);
        Assert.Equal("456", edit.Form["video_id"]);
        Assert.Equal("by_link", edit.Form["privacy_view"]);
        Assert.Equal("Йога-пикник", edit.Form["name"]);
        Assert.Equal("video-app-token", edit.Form["access_token"]);
        // Описание не отправляется: пустое значение стёрло бы текущее.
        Assert.DoesNotContain("desc", edit.Form.Keys);
    }

    [Fact]
    public async Task Video_privacy_readback_treats_a_missing_field_as_unknown()
    {
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var url = request.RequestUri!.ToString();
            await ApiCall.FromAsync(request, cancellationToken);
            if (url.Contains("act=web_token", StringComparison.Ordinal))
                return Json("""{"type":"okay","data":{"access_token":"video-app-token","expires":1800,"user_id":42}}""");
            if (url.Contains("video.edit", StringComparison.Ordinal))
                return Json("""{"response":{"success":1}}""");
            return Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":456}]}}""");
        });

        await using var client = Client(api);
        var result = await client.Live.SetVideoPrivacyAsync(-123, 456, VkLivePrivacy.ByLink);

        Assert.True(result.Accepted);
        Assert.Null(result.Privacy);
        Assert.False(result.Confirms(VkLivePrivacy.ByLink));
    }

    [Fact]
    public async Task Video_edit_access_key_confirms_link_only_when_get_hides_privacy()
    {
        var api = new RecordingHandler(async (request, cancellationToken) =>
        {
            var url = request.RequestUri!.ToString();
            await ApiCall.FromAsync(request, cancellationToken);
            if (url.Contains("act=web_token", StringComparison.Ordinal))
                return Json("""{"type":"okay","data":{"access_token":"video-app-token","expires":1800,"user_id":42}}""");
            if (url.Contains("video.edit", StringComparison.Ordinal))
                return Json("""{"response":{"success":1,"access_key":"link-only-key"}}""");
            return Json("""{"response":{"count":1,"items":[{"owner_id":-123,"id":456}]}}""");
        });

        await using var client = Client(api);
        var result = await client.Live.SetVideoPrivacyAsync(-123, 456, VkLivePrivacy.ByLink);

        Assert.True(result.Accepted);
        Assert.Equal("by_link", result.Privacy);
        Assert.Equal("link-only-key", result.AccessKey);
        Assert.True(result.Confirms(VkLivePrivacy.ByLink));
    }

    private static VkClient Client(HttpMessageHandler api, HttpMessageHandler? uploads = null)
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
            AuthenticatorFactory = _ => new NeverInteractiveAuthenticator(),
            ApiHttpMessageHandlerFactory = () => api,
            UploadHttpMessageHandlerFactory = () => uploads ?? new RecordingHandler(
                (_, _) => throw new InvalidOperationException("Upload was not expected."))
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
