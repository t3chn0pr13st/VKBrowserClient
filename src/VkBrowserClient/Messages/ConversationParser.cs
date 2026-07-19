using System.Text.Json;

namespace VkBrowserClient;

/// <summary>Разбор ответа messages.getConversations в модель <see cref="ConversationsPage"/>.</summary>
internal static class ConversationParser
{
    public static ConversationsPage Parse(JsonElement response)
    {
        var profiles = ProfileMaps.ReadProfiles(response);
        var groups = ProfileMaps.ReadGroups(response);

        var items = new List<Conversation>();
        if (response.TryGetProperty("items", out var itemsArr) && itemsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsArr.EnumerateArray())
            {
                if (!item.TryGetProperty("conversation", out var conv) || !conv.TryGetProperty("peer", out var peer))
                    continue;

                var peerId = peer.TryGetProperty("id", out var pid) && pid.TryGetInt64(out var pv) ? pv : 0;
                var peerType = PeerTypes.Parse(peer.TryGetProperty("type", out var t) ? t.GetString() : null);

                var title = peerType switch
                {
                    VkPeerType.Chat => conv.TryGetProperty("chat_settings", out var cs) && cs.TryGetProperty("title", out var ti)
                        ? ti.GetString() ?? $"Чат {peerId}"
                        : $"Чат {peerId}",
                    VkPeerType.User => profiles.TryGetValue(peerId, out var un) && un.Length > 0 ? un : $"id{peerId}",
                    VkPeerType.Group => groups.TryGetValue(Math.Abs(peerId), out var gn) ? gn : $"club{Math.Abs(peerId)}",
                    _ => $"peer {peerId}",
                };

                int unread = conv.TryGetProperty("unread_count", out var uc) && uc.TryGetInt32(out var ucv) ? ucv : 0;

                string? lastText = null;
                if (item.TryGetProperty("last_message", out var lm) && lm.TryGetProperty("text", out var lt))
                    lastText = lt.GetString();

                items.Add(new Conversation
                {
                    PeerType = peerType,
                    PeerId = peerId,
                    Title = title,
                    LastMessageText = string.IsNullOrEmpty(lastText) ? null : lastText,
                    UnreadCount = unread,
                });
            }
        }

        var total = response.TryGetProperty("count", out var cnt) && cnt.TryGetInt32(out var cv) ? cv : items.Count;
        return new ConversationsPage { TotalCount = total, Items = items };
    }
}
