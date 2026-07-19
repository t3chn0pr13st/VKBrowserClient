namespace VkBrowserClient;

/// <summary>Результат публикации клипа.</summary>
public sealed class VkClipResult
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }

    /// <summary>Ссылка-вложение вида video{owner}_{id}.</summary>
    public string Reference => $"video{OwnerId}_{VideoId}";

    /// <summary>Ссылка на клип вида https://vk.ru/clip{owner}_{id}.</summary>
    public string Url => $"https://vk.ru/clip{OwnerId}_{VideoId}";
}
