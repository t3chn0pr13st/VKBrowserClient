namespace VkBrowserClient;

/// <summary>Результат публикации записи на стене.</summary>
public sealed class WallPostResult
{
    /// <summary>id созданной записи.</summary>
    public required long PostId { get; init; }

    /// <summary>id владельца стены (обычно текущий пользователь).</summary>
    public long OwnerId { get; init; }

    /// <summary>Ссылка на запись вида https://vk.ru/wall{owner}_{post}.</summary>
    public string Url => $"https://vk.ru/wall{OwnerId}_{PostId}";

    /// <summary>Ссылка-вложение/идентификатор вида wall{owner}_{post}.</summary>
    public string Reference => $"wall{OwnerId}_{PostId}";
}
