namespace VkBrowserClient;

/// <summary>Нормализованное состояние объекта трансляции/записи VK.</summary>
public enum VkLiveStatusState
{
    /// <summary>Объект не найден или недоступен с переданным access_key.</summary>
    NotFound,

    /// <summary>VK помечает трансляцию как предстоящую.</summary>
    Upcoming,

    /// <summary>VK сообщает, что эфир идёт сейчас.</summary>
    Live,

    /// <summary>VK обрабатывает запись после эфира.</summary>
    Processing,

    /// <summary>Запись готова к просмотру.</summary>
    Ready,

    /// <summary>Ответ существует, но его состояние не удалось однозначно определить.</summary>
    Unknown,
}

/// <summary>Изображение/обложка из ответа VK Видео.</summary>
public sealed record VkLiveImage
{
    public required string Url { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Size { get; init; }
}

/// <summary>Текущее состояние трансляции или сохранённой после неё записи.</summary>
public sealed class VkLiveStatus
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }
    public required VkLiveStatusState State { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }

    /// <summary>Ключ доступа, если VK вернул его. Хранить как секрет.</summary>
    public string? AccessKey { get; init; }

    /// <summary>Готовый provider embed/player URL, если он уже доступен.</summary>
    public string? PlayerUrl { get; init; }

    public bool IsPrivate { get; init; }
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
    public long Spectators { get; init; }
    public DateTimeOffset? ScheduledStartAt { get; init; }
    public string? VideoType { get; init; }
    public IReadOnlyList<VkLiveImage> Images { get; init; } = [];

    public string Reference => $"video{OwnerId}_{VideoId}";
    public string Url => $"https://vk.ru/video{OwnerId}_{VideoId}";

    /// <summary>Безопасное строковое представление без access_key/player URL.</summary>
    public override string ToString() => $"{Reference} ({State})";
}
