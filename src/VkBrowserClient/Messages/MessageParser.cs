using System.Text.Json;

namespace VkBrowserClient;

/// <summary>Разбор ответов messages.getHistory и messages.send.</summary>
internal static class MessageParser
{
    public static MessageHistoryPage ParseHistory(JsonElement response)
    {
        var profiles = ProfileMaps.ReadProfiles(response);
        var groups = ProfileMaps.ReadGroups(response);

        var items = new List<VkMessage>();
        if (response.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
                items.Add(ParseMessage(m, profiles, groups));
        }

        var total = response.TryGetProperty("count", out var cnt) && cnt.TryGetInt32(out var cv) ? cv : items.Count;
        return new MessageHistoryPage { TotalCount = total, Items = items };
    }

    private static VkMessage ParseMessage(JsonElement m, Dictionary<long, string> profiles, Dictionary<long, string> groups)
    {
        var fromId = m.TryGetProperty("from_id", out var fi) && fi.TryGetInt64(out var fv) ? fv : 0;
        var date = m.TryGetProperty("date", out var d) && d.TryGetInt64(out var dv)
            ? DateTimeOffset.FromUnixTimeSeconds(dv) : DateTimeOffset.MinValue;

        var photos = new List<VkPhotoAttachment>();
        int otherAttachments = 0;
        if (m.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in atts.EnumerateArray())
            {
                var type = a.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "photo" && a.TryGetProperty("photo", out var photo))
                    photos.Add(ParsePhoto(photo));
                else
                    otherAttachments++;
            }
        }

        string? senderName = fromId >= 0
            ? (profiles.TryGetValue(fromId, out var pn) ? pn : null)
            : (groups.TryGetValue(Math.Abs(fromId), out var gn) ? gn : null);

        return new VkMessage
        {
            Id = m.TryGetProperty("id", out var id) && id.TryGetInt64(out var iv) ? iv : 0,
            FromId = fromId,
            Date = date,
            IsOutgoing = m.TryGetProperty("out", out var o) && o.TryGetInt32(out var ov) && ov == 1,
            Text = m.TryGetProperty("text", out var txt) ? txt.GetString() : null,
            SenderName = senderName,
            Photos = photos,
            OtherAttachmentsCount = otherAttachments,
            ConversationMessageId = m.TryGetProperty("conversation_message_id", out var c) && c.TryGetInt32(out var cmid) ? cmid : 0,
        };
    }

    private static VkPhotoAttachment ParsePhoto(JsonElement photo)
    {
        var sizes = new List<VkPhotoSize>();
        if (photo.TryGetProperty("sizes", out var sz) && sz.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sz.EnumerateArray())
            {
                var url = s.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url))
                    continue;
                sizes.Add(new VkPhotoSize
                {
                    Type = s.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "",
                    Url = url,
                    Width = s.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv) ? wv : 0,
                    Height = s.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? hv : 0,
                });
            }
        }

        // Наибольший размер по площади — это «оригинал» для показа/скачивания.
        var best = sizes.OrderByDescending(s => (long)s.Width * s.Height).FirstOrDefault();

        // Резерв: orig_photo, если sizes пуст.
        string bestUrl = best?.Url ?? "";
        int bestW = best?.Width ?? 0, bestH = best?.Height ?? 0;
        if (string.IsNullOrEmpty(bestUrl) && photo.TryGetProperty("orig_photo", out var orig) && orig.TryGetProperty("url", out var ou))
        {
            bestUrl = ou.GetString() ?? "";
            bestW = orig.TryGetProperty("width", out var ow) && ow.TryGetInt32(out var owv) ? owv : 0;
            bestH = orig.TryGetProperty("height", out var oh) && oh.TryGetInt32(out var ohv) ? ohv : 0;
        }

        return new VkPhotoAttachment
        {
            Id = photo.TryGetProperty("id", out var id) && id.TryGetInt64(out var iv) ? iv : 0,
            OwnerId = photo.TryGetProperty("owner_id", out var oid) && oid.TryGetInt64(out var ov) ? ov : 0,
            AccessKey = photo.TryGetProperty("access_key", out var ak) ? ak.GetString() : null,
            Url = bestUrl,
            Width = bestW,
            Height = bestH,
            Sizes = sizes,
        };
    }

    /// <summary>
    /// messages.send возвращает id отправленного сообщения в одном из форматов:
    ///  • объект {"cmid":N,"message_id":M} (текущий веб-формат для одного peer);
    ///  • просто число (старый формат);
    ///  • массив [{peer_id, message_id, …}] при пакетной отправке на несколько peer.
    /// Возвращаем message_id (глобальный id сообщения).
    /// </summary>
    public static long ParseSendResult(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object)
        {
            if (response.TryGetProperty("message_id", out var mid) && mid.TryGetInt64(out var mv))
                return mv;
            if (response.TryGetProperty("cmid", out var cmid) && cmid.TryGetInt64(out var cv))
                return cv;
        }

        if (response.ValueKind == JsonValueKind.Number && response.TryGetInt64(out var idNum))
            return idNum;

        if (response.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in response.EnumerateArray())
            {
                if (el.TryGetProperty("message_id", out var mid) && mid.TryGetInt64(out var mv))
                    return mv;
                if (el.TryGetProperty("conversation_message_id", out var ccmid) && ccmid.TryGetInt64(out var cv))
                    return cv;
            }
        }

        return 0;
    }
}
