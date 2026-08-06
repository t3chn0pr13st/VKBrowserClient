namespace VkBrowserClient;

/// <summary>
/// Сохраняемый этап установки обложки: подписанный upload URL уже получен.
/// <see cref="UploadUrl"/> является секретом до завершения загрузки.
/// </summary>
public sealed class VkLiveThumbnailUploadSession
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }
    public required string UploadUrl { get; init; }

    /// <summary>Безопасное строковое представление без подписанного URL.</summary>
    public override string ToString() => $"VkLiveThumbnailUploadSession {{ Video = video{OwnerId}_{VideoId}, UploadUrl = [REDACTED] }}";
}

/// <summary>
/// Сохраняемый ответ upload-сервера. Поле <see cref="ThumbJson"/> передаётся только
/// в <c>video.saveUploadedThumb</c> и не должно выводиться в operator-facing логи.
/// </summary>
public sealed class VkLiveThumbnailUpload
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }
    public required string ThumbJson { get; init; }
    public string? ThumbSize { get; init; }
    public string? RandomTag { get; init; }

    /// <summary>Безопасное строковое представление без thumb_json/random_tag.</summary>
    public override string ToString() => $"VkLiveThumbnailUpload {{ Video = video{OwnerId}_{VideoId}, ThumbJson = [REDACTED] }}";
}

/// <summary>Результат сохранения обложки VK.</summary>
public sealed record VkLiveThumbnailResult
{
    public required long PhotoId { get; init; }
    public long? PhotoOwnerId { get; init; }

    /// <summary>Provider hash обложки. Не требуется для повторного сохранения.</summary>
    public required string PhotoHash { get; init; }

    public IReadOnlyList<VkLiveImage> Images { get; init; } = [];
}
