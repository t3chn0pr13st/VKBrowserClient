namespace VkBrowserClient;

/// <summary>Состояние обработки видео/клипа на стороне VK.</summary>
public enum VkVideoProcessingState
{
    /// <summary>VK продолжает обработку файла.</summary>
    Processing,

    /// <summary>Видео готово к просмотру.</summary>
    Ready,

    /// <summary>Видео с указанным идентификатором не найдено.</summary>
    NotFound,
}

/// <summary>Результат проверки обработки видео/клипа.</summary>
public sealed class VkVideoProcessingResult
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }
    public required VkVideoProcessingState State { get; init; }
}
