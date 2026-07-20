namespace VkBrowserClient;

/// <summary>Работа с личными сообщениями и беседами.</summary>
public sealed class VkMessagesService
{
    private readonly VkClient _client;

    internal VkMessagesService(VkClient client) => _client = client;

    /// <summary>Последние беседы (диалоги) с человекочитаемыми названиями.</summary>
    public async Task<ConversationsPage> GetConversationsAsync(int count = 10, CancellationToken cancellationToken = default)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new Dictionary<string, string>
        {
            ["count"] = Math.Clamp(count, 1, 200).ToString(),
            ["extended"] = "1",
            ["fields"] = "first_name,last_name,name",
        };
        using var doc = await api.CallAsync("messages.getConversations", parameters, cancellationToken).ConfigureAwait(false);
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return ConversationParser.Parse(VkWebApi.GetResponseOrThrow(doc, "messages.getConversations"));
    }

    /// <summary>
    /// История сообщений диалога (по умолчанию 20 последних) с фото-вложениями.
    /// <paramref name="peerId"/> — id пользователя, отрицательный id сообщества или 2000000000+chat_id.
    /// </summary>
    public async Task<MessageHistoryPage> GetHistoryAsync(long peerId, int count = 20, int offset = 0, CancellationToken cancellationToken = default)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new Dictionary<string, string>
        {
            ["peer_id"] = peerId.ToString(),
            ["count"] = Math.Clamp(count, 1, 200).ToString(),
            ["offset"] = offset.ToString(),
            ["extended"] = "1",
            ["fields"] = "first_name,last_name,name",
        };
        using var doc = await api.CallAsync("messages.getHistory", parameters, cancellationToken).ConfigureAwait(false);
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return MessageParser.ParseHistory(VkWebApi.GetResponseOrThrow(doc, "messages.getHistory"));
    }

    /// <summary>
    /// Отправить сообщение с фотографиями (загрузятся автоматически).
    /// Для файлов/видео/клипов используйте перегрузку с <see cref="VkAttachmentSource"/>.
    /// </summary>
    /// <returns>id отправленного сообщения.</returns>
    public Task<long> SendMessageAsync(
        long peerId, string? text, IReadOnlyList<VkImage>? photos = null, CancellationToken cancellationToken = default)
        => SendMessageAsync(peerId, text, photos, randomId: null, cancellationToken);

    /// <summary>
    /// Отправить сообщение с фотографиями и заданным <paramref name="randomId"/> для идемпотентных повторов.
    /// Один и тот же положительный идентификатор не создаёт дубликаты при повторной отправке.
    /// </summary>
    public Task<long> SendMessageAsync(
        long peerId,
        string? text,
        IReadOnlyList<VkImage>? photos,
        int? randomId,
        CancellationToken cancellationToken = default)
        => SendMessageAsync(peerId, text, AttachmentUploads.FromPhotos(photos), randomId, cancellationToken);

    /// <summary>
    /// Отправить сообщение с произвольными вложениями — фото, документы (файлы/GIF/аудиосообщения) и видео/клипы.
    /// Медиа загружаются автоматически. Положительный <paramref name="randomId"/> делает повтор идемпотентным.
    /// </summary>
    /// <returns>id отправленного сообщения.</returns>
    public async Task<long> SendMessageAsync(
        long peerId,
        string? text,
        IReadOnlyList<VkAttachmentSource> attachments,
        int? randomId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (string.IsNullOrEmpty(text) && attachments.Count == 0)
            throw new ArgumentException("Нужен текст сообщения или хотя бы одно вложение.");
        if (randomId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(randomId), "random_id должен быть положительным.");

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        var refs = await AttachmentUploads.ResolveAllAsync(
            api,
            attachments,
            peerId,
            communityId: null,
            cancellationToken).ConfigureAwait(false);

        var parameters = new Dictionary<string, string>
        {
            ["peer_id"] = peerId.ToString(),
            // random_id защищает от повторной отправки при ретраях.
            ["random_id"] = (randomId ?? Random.Shared.Next(1, int.MaxValue)).ToString(),
        };
        if (!string.IsNullOrEmpty(text))
            parameters["message"] = text;
        if (refs.Count > 0)
            parameters["attachment"] = string.Join(",", refs);

        using var doc = await api.CallAsync("messages.send", parameters, cancellationToken).ConfigureAwait(false);
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return MessageParser.ParseSendResult(VkWebApi.GetResponseOrThrow(doc, "messages.send"));
    }
}
