using System.Net;

using static VkBrowserClient.Tests.LiveSdkHarness;

namespace VkBrowserClient.Tests;

public sealed class VkGroupsServiceTests
{
    [Fact]
    public async Task Reads_community_permissions_without_publishing_anything()
    {
        var calls = new List<IReadOnlyDictionary<string, string>>();
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => throw new InvalidOperationException("live-SDK здесь ни при чём.")),
            api: Handler(async (request, ct) =>
            {
                calls.Add(await FormAsync(request, ct));
                return Json("""
                    {"response":{"groups":[
                      {"id":59868532,"name":"Академия","can_post":1,"is_admin":true,"admin_level":3}
                    ]}}
                    """);
            }));

        var permissions = await client.Groups.GetPermissionsAsync(59868532);

        var call = Assert.Single(calls);
        Assert.Equal("59868532", call["group_ids"]);
        Assert.Equal("can_post", call["fields"]);

        Assert.Equal(59868532, permissions.GroupId);
        Assert.Equal("Академия", permissions.Name);
        Assert.True(permissions.CanPost);
        Assert.True(permissions.IsAdmin);
        Assert.Equal(3, permissions.AdminLevel);
    }

    [Fact]
    public async Task Reads_the_older_response_shape_without_the_groups_wrapper()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => throw new InvalidOperationException("live-SDK здесь ни при чём.")),
            api: Handler((_, _) => Task.FromResult(Json(
                """{"response":[{"id":7,"name":"Клуб","can_post":0}]}"""))));

        var permissions = await client.Groups.GetPermissionsAsync(7);

        Assert.False(permissions.CanPost);
        Assert.False(permissions.IsAdmin);
    }

    [Fact]
    public async Task Refuses_an_empty_result_rather_than_reporting_no_rights()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => throw new InvalidOperationException("live-SDK здесь ни при чём.")),
            api: Handler((_, _) => Task.FromResult(Json("""{"response":{"groups":[]}}"""))));

        // Пустой ответ и «прав нет» — разные вещи: молча вернуть CanPost=false значило бы
        // выдать сбой за отрицательный результат проверки.
        var error = await Assert.ThrowsAsync<VkClientException>(
            () => client.Groups.GetPermissionsAsync(59868532));

        Assert.Contains("59868532", error.Message);
    }

    [Fact]
    public async Task Rejects_a_non_positive_group_id()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => throw new InvalidOperationException("live-SDK здесь ни при чём.")),
            api: Handler((_, _) => throw new InvalidOperationException("Запроса быть не должно.")));

        await Assert.ThrowsAsync<ArgumentException>(() => client.Groups.GetPermissionsAsync(0));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Groups.GetPermissionsAsync(-59868532));
    }

    [Fact]
    public async Task Surfaces_a_vk_api_error_as_VkApiException()
    {
        await using var client = Client(
            SessionWithSdkToken(),
            sdk: Handler((_, _) => throw new InvalidOperationException("live-SDK здесь ни при чём.")),
            api: Handler((_, _) => Task.FromResult(Json(
                """{"error":{"error_code":15,"error_msg":"Access denied"}}""",
                HttpStatusCode.OK))));

        var error = await Assert.ThrowsAsync<VkApiException>(
            () => client.Groups.GetPermissionsAsync(59868532));

        Assert.Equal(15, error.ErrorCode);
    }
}
