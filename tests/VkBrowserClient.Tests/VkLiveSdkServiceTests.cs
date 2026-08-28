using System.Text.Json;

using static VkBrowserClient.Tests.LiveSdkHarness;

namespace VkBrowserClient.Tests;

public sealed class VkLiveSdkServiceTests
{
    /// <summary>Ответ, снятый с живого создания эфира (сокращённый до используемых полей).</summary>
    private const string CreatedStream = """
        {"data":{
          "credentials":{"streamKey":"1125900038046283_71_7z5sb46dzq","streamServer":"rtmp://vsu.mycdn.me/input/"},
          "channel":{"channelUrl":"channel35338325","id":35338325},
          "video":{"vkOwnerId":-59868532,"vkPostId":0,"vkVideoId":456239773},
          "streamSlot":{
            "id":163026,"slotUrl":"sl_163026","isTemporary":true,"isDeleted":false,
            "title":"ТЕСТ slotUrl","vkPermission":"by_link","isOnline":false
          }
        }}
        """;

    [Fact]
    public async Task Creates_a_group_stream_with_privacy_in_the_creating_request()
    {
        SdkCall? call = null;
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler(async (request, ct) =>
            {
                call = await SdkCall.FromAsync(request, ct);
                return Json(CreatedStream);
            }));

        var stream = await client.LiveSdk.CreateGroupStreamAsync(new VkLiveSdkCreateOptions
        {
            GroupId = 59868532,
            Title = "ТЕСТ slotUrl",
            Permission = VkLiveSdkPermission.ByLink,
            ChannelName = "Академия Кундалини Йоги",
            RecordStream = true,
        });

        // Ни канала, ни слота в пути нет — канал определяется по vk_group_id в теле.
        Assert.Equal("/v1/channel/manage/vk/stream/", call!.Path);
        Assert.Equal("59868532", call.Form["vk_group_id"]);

        // Главное: приватность едет в создающем запросе, а не отдельным PUT после.
        Assert.Equal("by_link", call.Form["vk_permissions"]);
        Assert.Equal("Академия Кундалини Йоги", call.Form["name"]);
        Assert.Equal("true", call.Form["is_should_record"]);
        Assert.Equal("false", call.Form["is_vk_wallpost_create"]);

        Assert.Equal("channel35338325", stream.ChannelUrl);
        Assert.Equal("sl_163026", stream.SlotUrl);
        Assert.Equal(163026, stream.SlotId);
        Assert.Equal(-59868532, stream.VkOwnerId);
        Assert.Equal(456239773, stream.VkVideoId);
        Assert.Equal(VkLiveSdkPermission.ByLink, stream.Permission);
        Assert.True(stream.IsTemporary);
        Assert.Equal("rtmp://vsu.mycdn.me/input/", stream.Ingest.Url);
        Assert.Equal("1125900038046283_71_7z5sb46dzq", stream.Ingest.Key);
        Assert.Equal("video-59868532_456239773", stream.Reference);
        Assert.Equal("https://vkvideo.ru/live-59868532_456239773", stream.Url);
    }

    [Fact]
    public async Task Sends_the_title_as_the_nested_editor_document_vk_expects()
    {
        SdkCall? call = null;
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler(async (request, ct) =>
            {
                call = await SdkCall.FromAsync(request, ct);
                return Json(CreatedStream);
            }));

        await client.LiveSdk.CreateGroupStreamAsync(new VkLiveSdkCreateOptions
        {
            GroupId = 59868532,
            Title = "Эфир \"в кавычках\"",
        });

        using var blocks = JsonDocument.Parse(call!.Form["title_data"]);
        var items = blocks.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("BLOCK_END", items[1].GetProperty("modificator").GetString());

        // Заголовок лежит ещё одним слоем JSON внутри content — кавычки должны пережить оба.
        using var content = JsonDocument.Parse(items[0].GetProperty("content").GetString()!);
        var parts = content.RootElement.EnumerateArray().ToArray();
        Assert.Equal("Эфир \"в кавычках\"", parts[0].GetString());
        Assert.Equal("unstyled", parts[1].GetString());
        Assert.Empty(parts[2].EnumerateArray());
    }

    [Theory]
    [InlineData(VkLiveSdkPermission.Public, "public")]
    [InlineData(VkLiveSdkPermission.Followers, "followers")]
    [InlineData(VkLiveSdkPermission.Admins, "admins")]
    [InlineData(VkLiveSdkPermission.ByLink, "by_link")]
    public async Task Maps_every_privacy_value(VkLiveSdkPermission permission, string expected)
    {
        SdkCall? call = null;
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler(async (request, ct) =>
            {
                call = await SdkCall.FromAsync(request, ct);
                return Json(CreatedStream);
            }));

        await client.LiveSdk.CreateGroupStreamAsync(new VkLiveSdkCreateOptions
        {
            GroupId = 1,
            Title = "t",
            Permission = permission,
        });

        Assert.Equal(expected, call!.Form["vk_permissions"]);
    }

    [Fact]
    public async Task Refuses_a_response_without_a_slot_instead_of_returning_a_broken_anchor()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => Task.FromResult(Json("""
                {"data":{"channel":{"channelUrl":"channel1"},"streamSlot":{"id":1},
                 "credentials":{"streamKey":"k","streamServer":"rtmp://s"}}}
                """))));

        var error = await Assert.ThrowsAsync<VkClientException>(
            () => client.LiveSdk.CreateGroupStreamAsync(new VkLiveSdkCreateOptions
            {
                GroupId = 1,
                Title = "t",
            }));

        Assert.Contains("slotUrl", error.Message);
    }

    [Fact]
    public async Task Refuses_a_response_without_ingest_credentials()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => Task.FromResult(Json("""
                {"data":{"channel":{"channelUrl":"channel1"},
                 "streamSlot":{"id":1,"slotUrl":"sl_1","vkPermission":"by_link"}}}
                """))));

        await Assert.ThrowsAsync<VkClientException>(
            () => client.LiveSdk.CreateGroupStreamAsync(new VkLiveSdkCreateOptions
            {
                GroupId = 1,
                Title = "t",
            }));
    }

    [Fact]
    public async Task Reads_back_the_actual_privacy_of_a_slot()
    {
        SdkCall? call = null;
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler(async (request, ct) =>
            {
                call = await SdkCall.FromAsync(request, ct);
                return Json("""{"data":{"streamSlot":{"slotUrl":"sl_1","vkPermission":"public","title":"Эфир ","isShouldRecord":true}}}""");
            }));

        var settings = await client.LiveSdk.GetStreamSettingsAsync("channel1", "sl_1");

        // Публичный /stream/slot/ отдаёт состояние для зрителя и настроек приватности не содержит.
        Assert.Equal("/v1/channel/channel1/manage/vk/stream/sl_1", call!.Path);
        Assert.Equal(VkLiveSdkPermission.Public, settings.Permission);
        // VK возвращает заголовок с хвостовым пробелом — он не должен утекать наружу.
        Assert.Equal("Эфир", settings.Title);
        Assert.True(settings.RecordStream);
    }

    [Fact]
    public async Task Finds_the_slot_when_the_response_puts_it_at_the_root()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => Task.FromResult(
                Json("""{"data":{"vkPermission":"by_link","title":"Прямо в корне","isShouldRecord":false}}"""))));

        var settings = await client.LiveSdk.GetStreamSettingsAsync("channel1", "sl_1");

        Assert.Equal(VkLiveSdkPermission.ByLink, settings.Permission);
        Assert.Equal("Прямо в корне", settings.Title);
        Assert.False(settings.RecordStream);
    }

    [Fact]
    public async Task Rejects_an_unknown_privacy_value_rather_than_guessing()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => Task.FromResult(
                Json("""{"data":{"streamSlot":{"vkPermission":"something_new","isShouldRecord":true}}}"""))));

        var error = await Assert.ThrowsAsync<VkClientException>(
            () => client.LiveSdk.GetStreamPermissionAsync("channel1", "sl_1"));

        Assert.Contains("something_new", error.Message);
    }

    [Fact]
    public async Task Validates_options_before_touching_the_network()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => throw new InvalidOperationException("Запроса быть не должно.")));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.LiveSdk.CreateGroupStreamAsync(new VkLiveSdkCreateOptions { GroupId = 0, Title = "t" }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.LiveSdk.CreateGroupStreamAsync(new VkLiveSdkCreateOptions { GroupId = 1, Title = "  " }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.LiveSdk.UpdateStreamAsync("channel1", "sl_1", new VkLiveSdkPatchOptions()));
    }

    [Theory]
    [InlineData(VkLiveSdkPermission.Public, VkLiveSdkPermission.ByLink, "by_link")]
    [InlineData(VkLiveSdkPermission.ByLink, VkLiveSdkPermission.Public, "public")]
    public async Task Updates_a_slot_with_a_full_preserved_form_and_reads_it_back(
        VkLiveSdkPermission before,
        VkLiveSdkPermission after,
        string expectedPermission)
    {
        var calls = 0;
        SdkCall? put = null;
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler(async (request, ct) =>
            {
                calls++;
                if (request.Method == HttpMethod.Put)
                {
                    put = await SdkCall.FromAsync(request, ct);
                    return Json("""{"data":{}}""");
                }

                return Json(FullSettings(calls == 1 ? before : after));
            }));

        var actual = await client.LiveSdk.UpdateStreamAsync("channel35338325", "sl_163026", new VkLiveSdkPatchOptions
        {
            Permission = after,
            Title = "Новый заголовок",
            Description = "Новое описание",
            RecordStream = false,
        });

        Assert.Equal(3, calls);
        Assert.Equal(VkLiveSdkPermission.Public == after ? VkLiveSdkPermission.Public : VkLiveSdkPermission.ByLink, actual.Permission);
        Assert.True(actual.RecordStream);
        Assert.Equal($"/v1/channel/channel35338325/manage/vk/stream/sl_163026", put!.Path);
        Assert.Equal("channel35338325", put.Form["channel_url"]);
        Assert.Equal("sl_163026", put.Form["slot_url"]);
        Assert.Equal(expectedPermission, put.Form["vk_permissions"]);
        Assert.Equal("42", put.Form["category_id"]);
        Assert.Equal("false", put.Form["is_infinite"]);
        Assert.Equal("false", put.Form["is_should_record"]);
        Assert.Equal("false", put.Form["is_playback_disabled"]);
        Assert.Equal("false", put.Form["is_vk_wallpost_create"]);
        Assert.Equal("https://example.test/info", put.Form["vk_additional_url"]);
        Assert.Equal("59868532", put.Form["vk_group_id"]);
        Assert.Equal("false", put.Form["use_stream_preview_mode"]);
        Assert.Equal("false", put.Form["is_chat_disabled"]);
        Assert.Equal("true", put.Form["is_vk_notify_followers"]);
        Assert.Equal("Новое описание", put.Form["vk_description"]);
        Assert.Equal("Академия", put.Form["name"]);
        Assert.Equal("2026-08-14T10:00:00Z", put.Form["planned_at"]);
        Assert.Equal("2026-08-14T12:00:00Z", put.Form["planned_end_at"]);
        using var titleBlocks = JsonDocument.Parse(put.Form["title_data"]);
        using var titleContent = JsonDocument.Parse(titleBlocks.RootElement[0].GetProperty("content").GetString()!);
        Assert.Equal("Новый заголовок", titleContent.RootElement[0].GetString());
    }

    [Fact]
    public async Task Returns_the_actual_readback_when_vk_does_not_apply_the_requested_permission()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((request, _) => Task.FromResult(
                request.Method == HttpMethod.Put
                    ? Json("""{"data":{}}""")
                    : Json(FullSettings(VkLiveSdkPermission.Public)))));

        var actual = await client.LiveSdk.UpdateStreamAsync("channel1", "sl_1", new VkLiveSdkPatchOptions
        {
            Permission = VkLiveSdkPermission.ByLink,
        });

        Assert.Equal(VkLiveSdkPermission.Public, actual.Permission);
    }

    [Fact]
    public async Task Returns_the_actual_readback_when_vk_does_not_enable_recording()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((request, _) => Task.FromResult(
                request.Method == HttpMethod.Put
                    ? Json("""{"data":{}}""")
                    : Json(FullSettings(VkLiveSdkPermission.Public, recordStream: false)))));

        var actual = await client.LiveSdk.UpdateStreamAsync("channel1", "sl_1", new VkLiveSdkPatchOptions
        {
            RecordStream = true,
        });

        Assert.False(actual.RecordStream);
    }

    [Fact]
    public async Task Preserves_the_live_settings_shape_returned_by_the_real_manage_endpoint()
    {
        var calls = 0;
        SdkCall? put = null;
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler(async (request, ct) =>
            {
                calls++;
                if (request.Method == HttpMethod.Put)
                {
                    put = await SdkCall.FromAsync(request, ct);
                    return Json("""{"data":{}}""");
                }

                return Json(RealManageSettings(calls == 1
                    ? VkLiveSdkPermission.ByLink
                    : VkLiveSdkPermission.Public));
            }));

        var actual = await client.LiveSdk.UpdateStreamAsync(
            "channel35338325",
            "sl_163026",
            new VkLiveSdkPatchOptions { Permission = VkLiveSdkPermission.Public });

        Assert.Equal(VkLiveSdkPermission.Public, actual.Permission);
        Assert.Equal("59868532", put!.Form["vk_group_id"]);
        Assert.Equal("false", put.Form["use_stream_preview_mode"]);
        Assert.Equal("42", put.Form["category_id"]);
    }

    [Fact]
    public async Task Refuses_to_put_when_the_settings_snapshot_is_incomplete()
    {
        var putCalled = false;
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((request, _) =>
            {
                putCalled |= request.Method == HttpMethod.Put;
                return Task.FromResult(Json("""{"data":{"streamSlot":{"vkPermission":"public","title":"Эфир","isShouldRecord":true}}}"""));
            }));

        var error = await Assert.ThrowsAsync<VkClientException>(() =>
            client.LiveSdk.UpdateStreamAsync("channel1", "sl_1", new VkLiveSdkPatchOptions
            {
                Permission = VkLiveSdkPermission.ByLink,
            }));

        Assert.Contains("vkGroupId", error.Message);
        Assert.False(putCalled);
    }

    [Fact]
    public async Task Propagates_a_failed_put_without_claiming_success()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((request, _) => Task.FromResult(
                request.Method == HttpMethod.Put
                    ? Json("""{"error":"rejected"}""", System.Net.HttpStatusCode.BadRequest)
                    : Json(FullSettings(VkLiveSdkPermission.Public)))));

        await Assert.ThrowsAsync<VkClientException>(() =>
            client.LiveSdk.UpdateStreamAsync("channel1", "sl_1", new VkLiveSdkPatchOptions
            {
                Permission = VkLiveSdkPermission.ByLink,
            }));
    }

    private static string FullSettings(VkLiveSdkPermission permission, bool recordStream = true)
    {
        var rawPermission = permission == VkLiveSdkPermission.ByLink ? "by_link" : "public";
        return """
            {"data":{"streamSlot":{
              "slotUrl":"sl_163026","vkPermission":"$PERMISSION","title":"Текущий заголовок",
              "categoryId":42,"isInfinite":false,"isShouldRecord":$RECORD_STREAM,
              "isPlaybackDisabled":false,"isVkWallpostCreate":false,
              "vkAdditionalUrl":"https://example.test/info","vkGroupId":59868532,
              "useStreamPreviewMode":false,"isChatDisabled":false,"isVkNotifyFollowers":true,
              "vkDescription":"Текущее описание","name":"Академия",
              "plannedAt":"2026-08-14T10:00:00Z","plannedEndAt":"2026-08-14T12:00:00Z"
            }}}
            """
            .Replace("$PERMISSION", rawPermission, StringComparison.Ordinal)
            .Replace("$RECORD_STREAM", recordStream ? "true" : "false", StringComparison.Ordinal);
    }

    private static string RealManageSettings(VkLiveSdkPermission permission)
    {
        var rawPermission = permission == VkLiveSdkPermission.ByLink ? "by_link" : "public";
        return """
            {"data":{
              "credentials":{"streamServer":"rtmp://example","streamKey":"secret"},
              "channel":{"id":35338325,"channelUrl":"channel35338325"},
              "streamSlot":{
                "slotUrl":"sl_163026","vkPermission":"$PERMISSION","title":"Текущий заголовок",
                "category":{"id":42,"title":"Education"},"isInfinite":false,"isShouldRecord":true,
                "isPlaybackDisabled":false,"isVkWallpostCreate":false,"vkAdditionalUrl":"",
                "usePreviewMode":false,"isChatDisabled":false,"isVkNotifyFollowers":false,
                "vkDescription":"Текущее описание","plannedAt":null,"plannedEndAt":null
              },
              "video":{"vkOwnerId":-59868532,"vkVideoId":456239773,"vkPostId":0}
            }}
            """.Replace("$PERMISSION", rawPermission, StringComparison.Ordinal);
    }
}
