namespace VkBrowserClient;

internal static class AttachmentUploads
{
    /// <summary>Обернуть список фото в общий тип вложений (для совместимых перегрузок).</summary>
    public static IReadOnlyList<VkAttachmentSource> FromPhotos(IReadOnlyList<VkImage>? photos)
        => photos is { Count: > 0 }
            ? photos.Select(VkAttachmentSource.Photo).ToList()
            : Array.Empty<VkAttachmentSource>();

    /// <summary>Загрузить все вложения и вернуть их строки (photo…/doc…/video…).</summary>
    public static async Task<List<string>> ResolveAllAsync(
        VkWebApi api, IReadOnlyList<VkAttachmentSource> attachments, long? peerId, CancellationToken ct)
    {
        var refs = new List<string>(attachments.Count);
        if (attachments.Count > 0)
        {
            var uploader = new VkMediaUploader(api);
            foreach (var a in attachments)
                refs.Add(await a.ResolveAsync(uploader, peerId, ct).ConfigureAwait(false));
        }
        return refs;
    }
}
