using System.Text.Json;

namespace VkBrowserClient;

internal static class PeerTypes
{
    public static VkPeerType Parse(string? type) => type switch
    {
        "user" => VkPeerType.User,
        "chat" => VkPeerType.Chat,
        "group" => VkPeerType.Group,
        _ => VkPeerType.Unknown,
    };
}

/// <summary>Чтение массивов profiles[] и groups[] из extended-ответа в словари id → имя.</summary>
internal static class ProfileMaps
{
    public static Dictionary<long, string> ReadProfiles(JsonElement response)
    {
        var map = new Dictionary<long, string>();
        if (response.TryGetProperty("profiles", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                if (!p.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
                    continue;
                var first = p.TryGetProperty("first_name", out var f) ? f.GetString() : null;
                var last = p.TryGetProperty("last_name", out var l) ? l.GetString() : null;
                map[id] = string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrEmpty(s))).Trim();
            }
        }
        return map;
    }

    public static Dictionary<long, string> ReadGroups(JsonElement response)
    {
        var map = new Dictionary<long, string>();
        if (response.TryGetProperty("groups", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in arr.EnumerateArray())
            {
                if (g.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id))
                    map[id] = g.TryGetProperty("name", out var n) ? n.GetString() ?? $"club{id}" : $"club{id}";
            }
        }
        return map;
    }
}
