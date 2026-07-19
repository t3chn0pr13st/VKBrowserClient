namespace VkBrowserClient;

/// <summary>Страница истории сообщений диалога.</summary>
public sealed class MessageHistoryPage
{
    /// <summary>Всего сообщений в диалоге (по данным API).</summary>
    public required int TotalCount { get; init; }

    /// <summary>Сообщения в порядке, который вернул API (по умолчанию — от самых свежих).</summary>
    public required IReadOnlyList<VkMessage> Items { get; init; }
}
