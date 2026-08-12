using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Сообщества: то немногое из <c>groups.*</c>, что нужно для проверки прав без публикации.
/// </summary>
public sealed class VkGroupsService
{
    private readonly VkClient _client;

    internal VkGroupsService(VkClient client) => _client = client;

    /// <summary>
    /// Прочитать права текущего аккаунта в сообществе.
    ///
    /// Смысл метода в том, что он ничего не публикует: до него единственным способом убедиться,
    /// что писать в сообщество разрешено, была реальная тестовая запись.
    /// </summary>
    public async Task<VkCommunityPermissions> GetPermissionsAsync(
        long groupId,
        CancellationToken cancellationToken = default)
    {
        if (groupId <= 0)
            throw new ArgumentException("Идентификатор сообщества должен быть положительным.", nameof(groupId));

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync(
            "groups.getById",
            new Dictionary<string, string>
            {
                ["group_ids"] = groupId.ToString(),
                ["fields"] = "can_post"
            },
            cancellationToken).ConfigureAwait(false);

        var response = VkWebApi.GetResponseOrThrow(doc, "groups.getById");
        // 5.28x заворачивает результат в "groups"; более старая форма — массив в корне.
        var groups = response.ValueKind == JsonValueKind.Object &&
                     response.TryGetProperty("groups", out var nested)
            ? nested
            : response;
        if (groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() == 0)
        {
            throw new VkClientException(
                $"groups.getById не вернул сообщество {groupId}: {VkSafeErrorDetails.Describe(response)}");
        }

        var group = groups[0];
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkCommunityPermissions
        {
            GroupId = groupId,
            Name = group.TryGetProperty("name", out var name) ? name.GetString() : null,
            CanPost = Flag(group, "can_post"),
            IsAdmin = Flag(group, "is_admin"),
            AdminLevel = group.TryGetProperty("admin_level", out var level) && level.TryGetInt32(out var value)
                ? value
                : 0
        };
    }

    /// <summary>VK отдаёт булевы поля числом 0/1, но иногда и настоящим boolean.</summary>
    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number > 0,
            _ => false
        };
}
