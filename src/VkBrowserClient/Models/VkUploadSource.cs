namespace VkBrowserClient;

/// <summary>
/// Повторно открываемый источник медиа для потоковой загрузки и безопасных ретраев.
/// Фабрика обязана возвращать новый читаемый поток при каждом вызове.
/// </summary>
public sealed class VkUploadSource
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _openReadAsync;

    private VkUploadSource(
        string fileName,
        string contentType,
        long length,
        Func<CancellationToken, ValueTask<Stream>> openReadAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(openReadAsync);

        FileName = Path.GetFileName(fileName);
        ContentType = contentType;
        Length = length;
        _openReadAsync = openReadAsync;
    }

    /// <summary>Имя файла, передаваемое upload-серверу.</summary>
    public string FileName { get; }

    /// <summary>MIME-тип файла.</summary>
    public string ContentType { get; }

    /// <summary>Точный размер файла в байтах.</summary>
    public long Length { get; }

    /// <summary>Создать повторно открываемый источник из файла без чтения файла целиком в память.</summary>
    public static VkUploadSource FromFile(string path, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("Файл для загрузки VK не найден.", fullPath);

        return Create(
            info.Name,
            contentType ?? GuessContentType(info.Name),
            info.Length,
            cancellationToken => ValueTask.FromResult<Stream>(new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan)));
    }

    /// <summary>Создать повторно открываемый источник из массива байтов.</summary>
    public static VkUploadSource FromBytes(byte[] bytes, string fileName, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Create(
            fileName,
            contentType ?? GuessContentType(fileName),
            bytes.LongLength,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));
    }

    /// <summary>
    /// Создать источник из пользовательской фабрики. Она должна возвращать новый читаемый
    /// поток на каждый вызов, поскольку загрузка может быть повторена после временной ошибки.
    /// </summary>
    public static VkUploadSource Create(
        string fileName,
        string contentType,
        long length,
        Func<CancellationToken, ValueTask<Stream>> openReadAsync) =>
        new(fileName, contentType, length, openReadAsync);

    internal async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        var stream = await _openReadAsync(cancellationToken).ConfigureAwait(false);
        if (stream is null)
            throw new VkClientException("Фабрика VkUploadSource вернула null.");
        if (!stream.CanRead)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new VkClientException("Фабрика VkUploadSource вернула нечитаемый поток.");
        }

        return stream;
    }

    internal static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".ogg" or ".oga" => "audio/ogg",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };
}
