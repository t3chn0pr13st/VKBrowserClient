namespace VkBrowserClient;

/// <summary>Один размер (превью) фотографии.</summary>
public sealed class VkPhotoSize
{
    /// <summary>Тип размера VK (s, m, x, y, z, w, o, p, q, r …).</summary>
    public required string Type { get; init; }
    public required string Url { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>Фотография-вложение в сообщении.</summary>
public sealed class VkPhotoAttachment
{
    public required long Id { get; init; }
    public required long OwnerId { get; init; }
    public string? AccessKey { get; init; }

    /// <summary>URL наибольшего доступного размера.</summary>
    public required string Url { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Все доступные размеры.</summary>
    public required IReadOnlyList<VkPhotoSize> Sizes { get; init; }

    /// <summary>Ссылка-идентификатор вида photo{owner}_{id}[_{access_key}] для пересылки/вложения.</summary>
    public string Reference => string.IsNullOrEmpty(AccessKey)
        ? $"photo{OwnerId}_{Id}"
        : $"photo{OwnerId}_{Id}_{AccessKey}";
}

/// <summary>Одно сообщение из истории диалога.</summary>
public sealed class VkMessage
{
    public required long Id { get; init; }

    /// <summary>id отправителя (отрицательный — сообщество).</summary>
    public required long FromId { get; init; }

    public required DateTimeOffset Date { get; init; }

    /// <summary>true — исходящее (отправлено вами).</summary>
    public required bool IsOutgoing { get; init; }

    public string? Text { get; init; }

    /// <summary>Имя отправителя, если удалось разрешить из extended-ответа.</summary>
    public string? SenderName { get; init; }

    /// <summary>Фотографии-вложения.</summary>
    public required IReadOnlyList<VkPhotoAttachment> Photos { get; init; }

    /// <summary>Число прочих (не фото) вложений — файлы, стикеры, ссылки и т.п.</summary>
    public int OtherAttachmentsCount { get; init; }

    /// <summary>id сообщения внутри беседы (conversation_message_id).</summary>
    public int ConversationMessageId { get; init; }
}
