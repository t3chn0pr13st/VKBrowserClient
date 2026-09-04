namespace VkBrowserClient;

/// <summary>Сохраняемый этап загрузки обычной длинной записи VK Видео.</summary>
public enum VkVideoUploadStage
{
    Created,
    Uploaded,
}

/// <summary>
/// Зарезервированный provider id и подписанный CDN URL. Экземпляр можно сериализовать
/// после каждого этапа и продолжить загрузку без повторного <c>video.save</c>.
/// </summary>
public sealed record VkVideoUploadSession
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }

    /// <summary>Ключ доступа к link-only записи. Хранить как секрет.</summary>
    public string? AccessKey { get; init; }

    /// <summary>Подписанный URL upload-сервера. Хранить как секрет.</summary>
    public required string UploadUrl { get; init; }

    public required VkVideoUploadStage Stage { get; init; }

    public string Reference => $"video{OwnerId}_{VideoId}";

    /// <summary>Безопасное представление без upload URL и ключа доступа.</summary>
    public override string ToString() => $"{Reference} ({Stage})";
}

/// <summary>Метаданные и видимость загружаемой длинной записи.</summary>
public sealed class VkVideoUploadOptions
{
    /// <summary>Положительный id сообщества; null означает личный профиль.</summary>
    public long? GroupId { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Кто может смотреть запись. Для платных занятий используйте ByLink.</summary>
    public VkLivePrivacy ViewPrivacy { get; init; } = VkLivePrivacy.ByLink;

    internal void Validate()
    {
        if (GroupId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(GroupId), "GroupId должен быть положительным.");
    }
}

/// <summary>Стабильная ссылка и состояние загруженной длинной записи VK Видео.</summary>
public sealed class VkVideoResult
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }
    public string? AccessKey { get; init; }
    public required VkVideoProcessingState State { get; init; }
    public string? PlayerUrl { get; init; }

    /// <summary>
    /// Признак неопубликованного черновика, если VK вернул <c>is_draft</c>.
    /// Черновик нельзя считать готовой записью даже при наличии player URL.
    /// </summary>
    public bool? IsDraft { get; init; }

    /// <summary>
    /// Приватность, подтверждённая readback-запросом. <see langword="null"/> означает,
    /// что VK не вернул поле; это не считается публичным доступом и не должно
    /// разблокировать публикацию записи.
    /// </summary>
    public string? PrivacyView { get; init; }

    public string Reference => $"video{OwnerId}_{VideoId}";

    public string Url => string.IsNullOrWhiteSpace(AccessKey)
        ? $"https://vkvideo.ru/{Reference}"
        : $"https://vkvideo.ru/{Reference}?access_key={Uri.EscapeDataString(AccessKey)}";

    public bool ConfirmsPrivacy(VkLivePrivacy expected) =>
        string.Equals(PrivacyView, VkLiveStartOptions.Privacy(expected), StringComparison.OrdinalIgnoreCase);

    /// <summary>Безопасное представление без access key и player URL.</summary>
    public override string ToString() => $"{Reference} ({State})";
}
