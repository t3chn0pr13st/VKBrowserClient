namespace VkBrowserClient;

/// <summary>Тип документа VK.</summary>
public enum VkDocType
{
    /// <summary>Обычный файл (в т.ч. GIF).</summary>
    Doc,
    /// <summary>Голосовое (аудио) сообщение.</summary>
    AudioMessage,
    /// <summary>Граффити.</summary>
    Graffiti,
}

/// <summary>
/// Медиа для загрузки и прикрепления к сообщению или записи на стене: фото, документ
/// (файл/GIF/аудиосообщение) или видео (в т.ч. вертикальный «клип»). Загрузка выполняется
/// автоматически при отправке — тем же способом, что и в веб-клиенте.
/// </summary>
public abstract class VkAttachmentSource
{
    /// <summary>Фотография.</summary>
    public static VkAttachmentSource Photo(VkImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Photo(VkUploadSource.FromBytes(image.Bytes, image.FileName, image.ContentType));
    }

    /// <summary>Фотография из повторно открываемого потокового источника.</summary>
    public static VkAttachmentSource Photo(VkUploadSource source) => new PhotoAttachmentSource(source);

    /// <summary>Фотография из файла без чтения файла целиком в память.</summary>
    public static VkAttachmentSource Photo(string path) => Photo(VkUploadSource.FromFile(path));

    /// <summary>Документ (файл, GIF, аудиосообщение, граффити).</summary>
    public static VkAttachmentSource Document(byte[] bytes, string fileName, VkDocType type = VkDocType.Doc)
        => Document(VkUploadSource.FromBytes(bytes, fileName), type);

    /// <summary>Документ из повторно открываемого потокового источника.</summary>
    public static VkAttachmentSource Document(VkUploadSource source, VkDocType type = VkDocType.Doc)
        => new DocumentAttachmentSource(source, type);

    /// <summary>Документ из файла без чтения файла целиком в память.</summary>
    public static VkAttachmentSource Document(string path, VkDocType type = VkDocType.Doc)
        => Document(VkUploadSource.FromFile(path), type);

    /// <summary>Видео (в т.ч. вертикальный короткий «клип»).</summary>
    public static VkAttachmentSource Video(byte[] bytes, string fileName, string? name = null, string? description = null)
        => Video(VkUploadSource.FromBytes(bytes, fileName, "video/mp4"), name, description);

    /// <summary>Видео из повторно открываемого потокового источника.</summary>
    public static VkAttachmentSource Video(VkUploadSource source, string? name = null, string? description = null)
        => new VideoAttachmentSource(source, name, description);

    /// <summary>Видео из файла без чтения файла целиком в память.</summary>
    public static VkAttachmentSource Video(string path, string? name = null, string? description = null)
        => Video(VkUploadSource.FromFile(path),
            name ?? Path.GetFileNameWithoutExtension(path), description);

    /// <summary>
    /// Загрузить медиа и вернуть строку-вложение (photo…/doc…/video…).
    /// <paramref name="peerId"/> задаётся при отправке в диалог (влияет на сервер загрузки), иначе null (стена).
    /// </summary>
    internal abstract Task<string> ResolveAsync(
        VkMediaUploader uploader,
        long? peerId,
        long? communityId,
        CancellationToken cancellationToken);
}

internal sealed class PhotoAttachmentSource(VkUploadSource source) : VkAttachmentSource
{
    internal override Task<string> ResolveAsync(
        VkMediaUploader uploader, long? peerId, long? communityId, CancellationToken cancellationToken)
        => uploader.UploadPhotoAsync(peerId, communityId, source, cancellationToken);
}

internal sealed class DocumentAttachmentSource(VkUploadSource source, VkDocType type) : VkAttachmentSource
{
    internal override Task<string> ResolveAsync(
        VkMediaUploader uploader, long? peerId, long? communityId, CancellationToken cancellationToken)
        => uploader.UploadDocumentAsync(peerId, communityId, source, type, cancellationToken);
}

internal sealed class VideoAttachmentSource(VkUploadSource source, string? name, string? description) : VkAttachmentSource
{
    internal override Task<string> ResolveAsync(
        VkMediaUploader uploader, long? peerId, long? communityId, CancellationToken cancellationToken)
        => uploader.UploadVideoAsync(communityId, source, name, description, cancellationToken);
}
