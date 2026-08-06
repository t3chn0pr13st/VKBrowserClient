namespace VkBrowserClient;

/// <summary>Секретные параметры входного потока, возвращённые VK.</summary>
public sealed class VkLiveIngest
{
    /// <summary>RTMP/RTMPS URL сервера приёма.</summary>
    public required string Url { get; init; }

    /// <summary>Секретный ключ потока. Не записывайте его в логи.</summary>
    public required string Key { get; init; }

    /// <summary>Альтернативный OKMP URL, если VK его вернул.</summary>
    public string? OkmpUrl { get; init; }

    /// <summary>WebRTC URL, если VK его вернул.</summary>
    public string? WebRtcUrl { get; init; }

    /// <summary>Безопасное строковое представление без URL и ключей.</summary>
    public override string ToString() => "VkLiveIngest { Url = [REDACTED], Key = [REDACTED] }";
}

/// <summary>Стабильная ссылка на объект трансляции VK.</summary>
public sealed class VkLiveReference
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }

    /// <summary>
    /// Ключ доступа к непубличному видео. Это секрет; он нужен для адресации video.get,
    /// но не включается в безопасные <see cref="Reference"/> и <see cref="Url"/>.
    /// </summary>
    public string? AccessKey { get; init; }

    /// <summary>Безопасная ссылка-вложение без access_key.</summary>
    public string Reference => $"video{OwnerId}_{VideoId}";

    /// <summary>Ссылка на страницу видео без access_key.</summary>
    public string Url => $"https://vk.ru/video{OwnerId}_{VideoId}";

    /// <summary>Идентификатор для API video.get, включающий access_key при наличии.</summary>
    internal string ApiReference => string.IsNullOrWhiteSpace(AccessKey)
        ? $"{OwnerId}_{VideoId}"
        : $"{OwnerId}_{VideoId}_{AccessKey}";

    /// <summary>Безопасное строковое представление без access_key.</summary>
    public override string ToString() => Reference;
}

/// <summary>Созданная или повторно открытая прямая трансляция VK Видео.</summary>
public sealed class VkLiveStream
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Ключ доступа к непубличному видео. Не записывайте его в логи.</summary>
    public required string AccessKey { get; init; }

    /// <summary>Параметры входного потока. URL и key должны храниться как секреты.</summary>
    public required VkLiveIngest Ingest { get; init; }

    /// <summary>Идентификатор автоматически созданной записи на стене.</summary>
    public long? PostId { get; init; }

    public string Reference => $"video{OwnerId}_{VideoId}";
    public string Url => $"https://vk.ru/video{OwnerId}_{VideoId}";

    /// <summary>Стабильная ссылка для последующих typed-операций.</summary>
    public VkLiveReference ToReference() => new()
    {
        OwnerId = OwnerId,
        VideoId = VideoId,
        AccessKey = AccessKey,
    };

    /// <summary>Безопасное строковое представление без ingest/access ключей.</summary>
    public override string ToString() => Reference;
}

/// <summary>Результат остановки прямой трансляции.</summary>
public sealed record VkLiveStopResult
{
    /// <summary>Число уникальных зрителей по ответу VK.</summary>
    public long UniqueViewers { get; init; }
}

/// <summary>Результат изменения метаданных/приватности трансляции.</summary>
public sealed class VkLiveUpdateResult
{
    public required bool Success { get; init; }

    /// <summary>Новый access_key, если VK его вернул. Хранить как секрет.</summary>
    public string? AccessKey { get; init; }

    /// <summary>Безопасное строковое представление без access_key.</summary>
    public override string ToString() => $"VkLiveUpdateResult {{ Success = {Success} }}";
}
