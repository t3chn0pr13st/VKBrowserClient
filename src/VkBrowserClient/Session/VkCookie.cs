namespace VkBrowserClient;

/// <summary>
/// Cookie сессии, снятая из браузера. Хранит ровно то, что нужно, чтобы
/// воспроизвести запросы к vk.ru / login.vk.ru из обычного HttpClient.
/// </summary>
public sealed class VkCookie
{
    public required string Name { get; init; }
    public required string Value { get; init; }

    /// <summary>Домен из браузера, например ".vk.ru" или "login.vk.ru".</summary>
    public required string Domain { get; init; }

    public string Path { get; init; } = "/";

    /// <summary>Unix-время истечения в секундах. -1 или null — сессионная cookie.</summary>
    public double? Expires { get; init; }

    public bool HttpOnly { get; init; }
    public bool Secure { get; init; }
    public string? SameSite { get; init; }
}
