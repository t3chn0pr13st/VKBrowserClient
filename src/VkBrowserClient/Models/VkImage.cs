namespace VkBrowserClient;

/// <summary>
/// Изображение для загрузки в VK (в пост на стену или в сообщение).
/// </summary>
public sealed class VkImage
{
    /// <summary>Содержимое файла изображения.</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>Имя файла, например «photo.jpg».</summary>
    public required string FileName { get; init; }

    /// <summary>MIME-тип содержимого.</summary>
    public string ContentType { get; init; } = "image/jpeg";

    /// <summary>Загрузить изображение из файла на диске.</summary>
    public static VkImage FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new VkImage
        {
            Bytes = File.ReadAllBytes(path),
            FileName = Path.GetFileName(path),
            ContentType = GuessContentType(path),
        };
    }

    /// <summary>Создать изображение из массива байтов.</summary>
    public static VkImage FromBytes(byte[] bytes, string fileName, string? contentType = null) => new()
    {
        Bytes = bytes,
        FileName = fileName,
        ContentType = contentType ?? GuessContentType(fileName),
    };

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };
}
