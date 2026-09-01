using System.Text.Json;
using System.Text.RegularExpressions;

namespace VkBrowserClient;

/// <summary>
/// Сообщества: то немногое из <c>groups.*</c>, что нужно для проверки прав без публикации.
/// </summary>
public sealed class VkGroupsService
{
    private static readonly Regex ScreenNamePattern = new(
        "^[A-Za-z0-9_.-]{2,64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

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

        return await GetPermissionsCoreAsync(
            groupId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            groupId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Прочитать сообщество и права текущего аккаунта по короткому адресу VK
    /// (например, <c>academyyoga_pr</c>). Возвращённый <see cref="VkCommunityPermissions.GroupId"/>
    /// можно использовать как канонический числовой идентификатор для публикации.
    /// </summary>
    public async Task<VkCommunityPermissions> GetPermissionsAsync(
        string screenName,
        CancellationToken cancellationToken = default)
    {
        var normalized = screenName?.Trim() ?? "";
        if (!ScreenNamePattern.IsMatch(normalized))
        {
            throw new ArgumentException(
                "Короткий адрес сообщества VK должен содержать только латинские буквы, цифры, точку, дефис или подчёркивание.",
                nameof(screenName));
        }

        return await GetPermissionsCoreAsync(normalized, expectedGroupId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VkCommunityPermissions> GetPermissionsCoreAsync(
        string groupReference,
        long? expectedGroupId,
        CancellationToken cancellationToken)
    {

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync(
            "groups.getById",
            new Dictionary<string, string>
            {
                ["group_ids"] = groupReference,
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
                $"groups.getById не вернул сообщество {groupReference}: {VkSafeErrorDetails.Describe(response)}");
        }

        var group = groups[0];
        if (!group.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt64(out var resolvedGroupId) ||
            resolvedGroupId <= 0)
        {
            throw new VkClientException(
                $"groups.getById не вернул числовой id сообщества {groupReference}: {VkSafeErrorDetails.Describe(group)}");
        }
        if (expectedGroupId is { } expected && resolvedGroupId != expected)
        {
            throw new VkClientException(
                $"groups.getById вернул неожиданное сообщество {resolvedGroupId} вместо {expected}.");
        }

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkCommunityPermissions
        {
            GroupId = resolvedGroupId,
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
