namespace VkBrowserClient;

/// <summary>Тип собеседника в беседе.</summary>
public enum VkPeerType
{
    Unknown = 0,
    /// <summary>Личный диалог с пользователем.</summary>
    User,
    /// <summary>Групповой чат.</summary>
    Chat,
    /// <summary>Диалог с сообществом.</summary>
    Group,
}

/// <summary>
/// Одна беседа из списка диалогов, с уже разрешённым человекочитаемым названием.
/// </summary>
public sealed class Conversation
{
    public required VkPeerType PeerType { get; init; }

    /// <summary>peer_id: id пользователя, отрицательный id сообщества или 2000000000+chat_id для чатов.</summary>
    public required long PeerId { get; init; }

    /// <summary>Название: имя собеседника, название сообщества или заголовок чата.</summary>
    public required string Title { get; init; }

    /// <summary>Текст последнего сообщения (может быть пустым, если это вложение).</summary>
    public string? LastMessageText { get; init; }

    /// <summary>Число непрочитанных сообщений в беседе.</summary>
    public int UnreadCount { get; init; }
}
