namespace VkBrowserClient;

/// <summary>Долговечный этап загрузки Клипа, пригодный для сериализации между рестартами worker.</summary>
public enum VkClipUploadStage
{
    /// <summary>Идентификатор и upload URL зарезервированы, файл ещё не принят CDN.</summary>
    Created,

    /// <summary>CDN принял файл; можно ожидать кодирование и публиковать тот же Clip id.</summary>
    Uploaded,
}

/// <summary>
/// Сериализуемая сессия загрузки Клипа. <see cref="UploadUrl"/> является подписанным URL
/// и должна храниться как секрет до завершения публикации.
/// </summary>
public sealed record VkClipUploadSession
{
    public required long OwnerId { get; init; }
    public required long VideoId { get; init; }
    public required string UploadUrl { get; init; }
    public string? VideoHash { get; init; }
    public VkClipUploadStage Stage { get; init; }

    public string Reference => $"video{OwnerId}_{VideoId}";
    public string Url => $"https://vk.ru/clip{OwnerId}_{VideoId}";
}
