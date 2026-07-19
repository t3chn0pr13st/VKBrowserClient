namespace VkBrowserClient;

/// <summary>Публикация записей на стене.</summary>
public sealed class VkWallService
{
    private readonly VkClient _client;

    internal VkWallService(VkClient client) => _client = client;

    /// <summary>
    /// Опубликовать запись на своей стене. Можно приложить фотографии (загрузятся автоматически).
    /// <paramref name="friendsOnly"/> — опубликовать только для друзей.
    /// </summary>
    public async Task<WallPostResult> PostAsync(
        string? text, IReadOnlyList<VkImage>? photos = null, bool friendsOnly = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) && (photos is null || photos.Count == 0))
            throw new ArgumentException("Нужен текст записи или хотя бы одно фото.");

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);

        var attachments = new List<string>();
        if (photos is { Count: > 0 })
        {
            var uploader = new VkPhotoUploader(api);
            foreach (var img in photos)
                attachments.Add(await uploader.UploadForWallAsync(img, cancellationToken).ConfigureAwait(false));
        }

        var parameters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(text))
            parameters["message"] = text;
        if (attachments.Count > 0)
            parameters["attachments"] = string.Join(",", attachments);
        if (friendsOnly)
            parameters["friends_only"] = "1";

        using var doc = await api.CallAsync("wall.post", parameters, cancellationToken).ConfigureAwait(false);
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);

        var response = VkWebApi.GetResponseOrThrow(doc, "wall.post");
        var postId = response.TryGetProperty("post_id", out var pid) && pid.TryGetInt64(out var v) ? v : 0;
        return new WallPostResult { PostId = postId, OwnerId = _client.UserId ?? 0 };
    }
}
