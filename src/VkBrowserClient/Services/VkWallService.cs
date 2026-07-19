namespace VkBrowserClient;

/// <summary>Публикация записей на стене.</summary>
public sealed class VkWallService
{
    private readonly VkClient _client;

    internal VkWallService(VkClient client) => _client = client;

    /// <summary>
    /// Опубликовать запись с фотографиями (загрузятся автоматически).
    /// Для файлов/видео/клипов используйте перегрузку с <see cref="VkAttachmentSource"/>.
    /// <paramref name="friendsOnly"/> — опубликовать только для друзей.
    /// </summary>
    public Task<WallPostResult> PostAsync(
        string? text, IReadOnlyList<VkImage>? photos = null, bool friendsOnly = false, CancellationToken cancellationToken = default)
        => PostAsync(text, AttachmentUploads.FromPhotos(photos), friendsOnly, cancellationToken);

    /// <summary>
    /// Опубликовать запись с произвольными вложениями — фото, документы и видео/клипы (загрузятся автоматически).
    /// <paramref name="friendsOnly"/> — опубликовать только для друзей.
    /// </summary>
    public async Task<WallPostResult> PostAsync(
        string? text, IReadOnlyList<VkAttachmentSource> attachments, bool friendsOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (string.IsNullOrEmpty(text) && attachments.Count == 0)
            throw new ArgumentException("Нужен текст записи или хотя бы одно вложение.");

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        var refs = await AttachmentUploads.ResolveAllAsync(api, attachments, peerId: null, cancellationToken).ConfigureAwait(false);

        var parameters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(text))
            parameters["message"] = text;
        if (refs.Count > 0)
            parameters["attachments"] = string.Join(",", refs);
        if (friendsOnly)
            parameters["friends_only"] = "1";

        using var doc = await api.CallAsync("wall.post", parameters, cancellationToken).ConfigureAwait(false);
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);

        var response = VkWebApi.GetResponseOrThrow(doc, "wall.post");
        var postId = response.TryGetProperty("post_id", out var pid) && pid.TryGetInt64(out var v) ? v : 0;
        return new WallPostResult { PostId = postId, OwnerId = _client.UserId ?? 0 };
    }
}
