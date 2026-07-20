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
    public Task<WallPostResult> PostAsync(
        string? text, IReadOnlyList<VkAttachmentSource> attachments, bool friendsOnly = false, CancellationToken cancellationToken = default)
        => PostCoreAsync(
            text,
            attachments,
            new VkWallPostOptions { FriendsOnly = friendsOnly },
            cancellationToken);

    /// <summary>
    /// Опубликовать запись с явными настройками стены, включая стену сообщества.
    /// Вложения загружаются в контекст выбранного сообщества и сохраняют исходный порядок.
    /// </summary>
    public Task<WallPostResult> PostAsync(
        string? text,
        IReadOnlyList<VkAttachmentSource> attachments,
        VkWallPostOptions options,
        CancellationToken cancellationToken = default) =>
        PostCoreAsync(text, attachments, options, cancellationToken);

    /// <summary>Опубликовать запись от имени сообщества.</summary>
    public Task<WallPostResult> PostToCommunityAsync(
        long communityId,
        string? text,
        IReadOnlyList<VkAttachmentSource> attachments,
        CancellationToken cancellationToken = default) =>
        PostCoreAsync(
            text,
            attachments,
            new VkWallPostOptions { CommunityId = communityId, FromCommunity = true },
            cancellationToken);

    /// <summary>
    /// Изменить только текст существующей записи. Перед изменением текущие вложения читаются
    /// через wall.getById и передаются обратно в wall.edit, чтобы VK гарантированно их сохранил.
    /// </summary>
    public async Task<WallPostResult> EditTextAsync(
        long ownerId,
        long postId,
        string? text,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == 0)
            throw new ArgumentOutOfRangeException(nameof(ownerId));
        if (postId <= 0)
            throw new ArgumentOutOfRangeException(nameof(postId));

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        var existingAttachments = await GetAttachmentReferencesAsync(api, ownerId, postId, cancellationToken)
            .ConfigureAwait(false);
        var parameters = new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["post_id"] = postId.ToString(),
            ["message"] = text ?? "",
        };
        if (existingAttachments.Count > 0)
            parameters["attachments"] = string.Join(",", existingAttachments);

        using var doc = await api.CallAsync("wall.edit", parameters, cancellationToken).ConfigureAwait(false);
        VkWebApi.GetResponseOrThrow(doc, "wall.edit");
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new WallPostResult { OwnerId = ownerId, PostId = postId };
    }

    private static async Task<IReadOnlyList<string>> GetAttachmentReferencesAsync(
        VkWebApi api,
        long ownerId,
        long postId,
        CancellationToken cancellationToken)
    {
        using var doc = await api.CallAsync("wall.getById", new Dictionary<string, string>
        {
            ["posts"] = $"{ownerId}_{postId}",
            ["extended"] = "0",
        }, cancellationToken).ConfigureAwait(false);
        var response = VkWebApi.GetResponseOrThrow(doc, "wall.getById");
        var posts = response.ValueKind == System.Text.Json.JsonValueKind.Array
            ? response
            : response.TryGetProperty("items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array
                ? items
                : default;
        if (posts.ValueKind != System.Text.Json.JsonValueKind.Array || posts.GetArrayLength() == 0)
            throw new VkClientException($"Запись wall{ownerId}_{postId} не найдена; изменение отменено.");

        var post = posts[0];
        if (!post.TryGetProperty("attachments", out var attachments) ||
            attachments.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Array.Empty<string>();

        var references = new List<string>(attachments.GetArrayLength());
        foreach (var attachment in attachments.EnumerateArray())
        {
            var type = attachment.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(type))
                throw new VkClientException("wall.getById вернул вложение без type; изменение отменено.");

            if (type == "link" &&
                attachment.TryGetProperty("link", out var link) &&
                link.TryGetProperty("url", out var url) &&
                !string.IsNullOrWhiteSpace(url.GetString()))
            {
                references.Add(url.GetString()!);
                continue;
            }

            if (!attachment.TryGetProperty(type, out var media) ||
                !media.TryGetProperty("owner_id", out var ownerElement) ||
                !ownerElement.TryGetInt64(out var mediaOwnerId) ||
                !media.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetInt64(out var mediaId) ||
                mediaId <= 0)
            {
                throw new VkClientException(
                    $"Вложение типа '{type}' нельзя безопасно восстановить; изменение отменено.");
            }

            var accessKey = media.TryGetProperty("access_key", out var accessKeyElement)
                ? accessKeyElement.GetString()
                : null;
            references.Add(string.IsNullOrWhiteSpace(accessKey)
                ? $"{type}{mediaOwnerId}_{mediaId}"
                : $"{type}{mediaOwnerId}_{mediaId}_{accessKey}");
        }

        return references;
    }

    private async Task<WallPostResult> PostCoreAsync(
        string? text,
        IReadOnlyList<VkAttachmentSource> attachments,
        VkWallPostOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(text) && attachments.Count == 0)
            throw new ArgumentException("Нужен текст записи или хотя бы одно вложение.");
        if (attachments.Count > 10)
            throw new ArgumentOutOfRangeException(nameof(attachments), "VK разрешает не более 10 вложений в записи.");

        var communityId = options.ValidateAndGetCommunityId();
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        var refs = await AttachmentUploads.ResolveAllAsync(
            api,
            attachments,
            peerId: null,
            communityId,
            cancellationToken).ConfigureAwait(false);

        var parameters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(text))
            parameters["message"] = text;
        if (refs.Count > 0)
            parameters["attachments"] = string.Join(",", refs);
        if (options.FriendsOnly)
            parameters["friends_only"] = "1";
        if (communityId is long groupId)
        {
            parameters["owner_id"] = (-groupId).ToString();
            if (options.FromCommunity)
                parameters["from_group"] = "1";
        }
        if (options.PublishAt is { } publishAt)
            parameters["publish_date"] = publishAt.ToUnixTimeSeconds().ToString();

        using var doc = await api.CallAsync("wall.post", parameters, cancellationToken).ConfigureAwait(false);
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);

        var response = VkWebApi.GetResponseOrThrow(doc, "wall.post");
        var postId = response.TryGetProperty("post_id", out var pid) && pid.TryGetInt64(out var v) ? v : 0;
        if (postId <= 0)
            throw new VkClientException("wall.post не вернул post_id.");
        return new WallPostResult
        {
            PostId = postId,
            OwnerId = communityId is long resultGroupId ? -resultGroupId : _client.UserId ?? 0
        };
    }
}
