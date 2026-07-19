namespace VkBrowserClient;

/// <summary>Страница списка бесед.</summary>
public sealed class ConversationsPage
{
    /// <summary>Всего бесед у пользователя (по данным API).</summary>
    public required int TotalCount { get; init; }

    /// <summary>Беседы в порядке от самой свежей.</summary>
    public required IReadOnlyList<Conversation> Items { get; init; }
}
