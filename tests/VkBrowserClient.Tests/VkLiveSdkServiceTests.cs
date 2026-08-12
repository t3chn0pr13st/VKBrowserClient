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
                return Json("""{"data":{"streamSlot":{"slotUrl":"sl_1","vkPermission":"public"}}}""");
            }));

        var permission = await client.LiveSdk.GetStreamPermissionAsync("channel1", "sl_1");

        Assert.Equal("/v1/channel/channel1/stream/slot/sl_1", call!.Path);
        Assert.Equal(VkLiveSdkPermission.Public, permission);
    }

    [Fact]
    public async Task Rejects_an_unknown_privacy_value_rather_than_guessing()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => Task.FromResult(
                Json("""{"data":{"streamSlot":{"vkPermission":"something_new"}}}"""))));

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
    }
}
