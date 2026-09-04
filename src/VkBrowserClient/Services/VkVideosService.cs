using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Обычные длинные записи VK Видео: резервирование id через <c>video.save</c>,
/// потоковая загрузка на CDN, отдельная установка приватности и проверка обработки.
/// Стена этим сервисом никогда не используется.
/// </summary>
public sealed class VkVideosService
{
    private readonly VkClient _client;

    internal VkVideosService(VkClient client) => _client = client;

    /// <summary>Полностью загрузить файл и применить требуемую приватность.</summary>
    public async Task<VkVideoResult> UploadFromFileAsync(
        string path,
        VkVideoUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var source = VkUploadSource.FromFile(path);
        return await UploadAsync(source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Полностью загрузить повторно открываемый поток и применить приватность.</summary>
    public async Task<VkVideoResult> UploadAsync(
        VkUploadSource video,
        VkVideoUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new VkVideoUploadOptions();
        var session = await CreateUploadSessionAsync(video, options, cancellationToken).ConfigureAwait(false);
        session = await UploadAsync(session, video, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(session, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Зарезервировать provider id. Сохраните результат до загрузки файла: повторный вызов
    /// <c>video.save</c> создаст ещё один объект и потому не является безопасным retry.
    /// </summary>
    public async Task<VkVideoUploadSession> CreateUploadSessionAsync(
        VkUploadSource video,
        VkVideoUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateVideo(video);
        options ??= new VkVideoUploadOptions();
        options.Validate();

        var parameters = new Dictionary<string, string>
        {
            ["name"] = string.IsNullOrWhiteSpace(options.Name) ? Path.GetFileNameWithoutExtension(video.FileName) : options.Name,
            ["description"] = options.Description ?? string.Empty,
            ["is_private"] = "0",
            ["wallpost"] = "0",
        };
        if (options.GroupId is long groupId)
            parameters["group_id"] = groupId.ToString();

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var document = await api.CallAsync("video.save", parameters, cancellationToken).ConfigureAwait(false);
        var response = VkWebApi.GetResponseOrThrow(document, "video.save");
        var uploadUrl = String(response, "upload_url");
        var ownerId = Int64(response, "owner_id");
        var videoId = Int64(response, "video_id");
        if (string.IsNullOrWhiteSpace(uploadUrl) || ownerId == 0 || videoId <= 0)
            throw new VkClientException("video.save не вернул upload_url/owner_id/video_id.");

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkVideoUploadSession
        {
            OwnerId = ownerId,
            VideoId = videoId,
            AccessKey = String(response, "access_key"),
            UploadUrl = uploadUrl,
            Stage = VkVideoUploadStage.Created,
        };
    }

    /// <summary>
    /// Загрузить файл в уже зарезервированный объект. Временные ошибки повторяют тот же
    /// signed URL и не создают новый provider id.
    /// </summary>
    public async Task<VkVideoUploadSession> UploadAsync(
        VkVideoUploadSession session,
        VkUploadSource video,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session, VkVideoUploadStage.Created);
        ValidateVideo(video);

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        await VkUploadRetry.ExecuteAsync(async () =>
        {
            using var upload = await api.UploadFileAsync(
                    session.UploadUrl,
                    "video_file",
                    video,
                    cancellationToken)
                .ConfigureAwait(false);
            if (upload.RootElement.TryGetProperty("error", out _))
                throw new VkClientException(
                    $"CDN отклонил видео: {VkSafeErrorDetails.Describe(upload.RootElement)}");
            return true;
        }, cancellationToken).ConfigureAwait(false);

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return session with { Stage = VkVideoUploadStage.Uploaded };
    }

    /// <summary>
    /// Применить приватность через приложение VK Видео и обязательно подтвердить её
    /// readback-запросом. Неподтверждённое by_link не считается успешной публикацией.
    /// </summary>
    public async Task<VkVideoResult> CompleteAsync(
        VkVideoUploadSession session,
        VkVideoUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session, VkVideoUploadStage.Uploaded);
        options ??= new VkVideoUploadOptions();
        options.Validate();

        var privacy = await _client.Live.SetVideoPrivacyAsync(
                session.OwnerId,
                session.VideoId,
                options.ViewPrivacy,
                options.Name,
                options.Description,
                cancellationToken)
            .ConfigureAwait(false);
        if (!privacy.Accepted || !privacy.Confirms(options.ViewPrivacy))
            throw new VkClientException(
                $"VK принял видео {session.Reference}, но не подтвердил privacy_view={VkLiveStartOptions.Privacy(options.ViewPrivacy)}.");

        return await GetStatusAsync(
                session.OwnerId,
                session.VideoId,
                session.AccessKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Проверить обработку записи по стабильному provider id.</summary>
    public async Task<VkVideoResult> GetStatusAsync(
        long ownerId,
        long videoId,
        string? accessKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);
        var reference = string.IsNullOrWhiteSpace(accessKey)
            ? $"{ownerId}_{videoId}"
            : $"{ownerId}_{videoId}_{accessKey}";
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var document = await api.CallAsync("video.get", new Dictionary<string, string>
        {
            ["videos"] = reference,
            ["count"] = "1",
        }, cancellationToken).ConfigureAwait(false);
        var response = VkWebApi.GetResponseOrThrow(document, "video.get");
        var item = response.TryGetProperty("items", out var items) &&
                   items.ValueKind == JsonValueKind.Array &&
                   items.GetArrayLength() > 0
            ? items[0]
            : default;

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new VkVideoResult
            {
                OwnerId = ownerId,
                VideoId = videoId,
                AccessKey = accessKey,
                State = VkVideoProcessingState.NotFound,
            };
        }

        var processing = Boolean(item, "processing") || Boolean(item, "converting");
        return new VkVideoResult
        {
            OwnerId = ownerId,
            VideoId = videoId,
            AccessKey = String(item, "access_key") ?? accessKey,
            State = processing ? VkVideoProcessingState.Processing : VkVideoProcessingState.Ready,
            PlayerUrl = String(item, "player"),
        };
    }

    public Task<VkVideoResult> GetStatusAsync(
        VkVideoUploadSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return GetStatusAsync(session.OwnerId, session.VideoId, session.AccessKey, cancellationToken);
    }

    private static void ValidateVideo(VkUploadSource video)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (video.Length < 16 * 1024)
            throw new ArgumentException("VK не принимает видео меньше 16 КБ.", nameof(video));
    }

    private static void ValidateSession(VkVideoUploadSession session, VkVideoUploadStage expected)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateReference(session.OwnerId, session.VideoId);
        if (string.IsNullOrWhiteSpace(session.UploadUrl))
            throw new ArgumentException("UploadUrl не может быть пустым.", nameof(session));
        if (session.Stage != expected)
            throw new ArgumentException($"Ожидался этап {expected}, получен {session.Stage}.", nameof(session));
    }

    private static void ValidateReference(long ownerId, long videoId)
    {
        if (ownerId == 0)
            throw new ArgumentOutOfRangeException(nameof(ownerId));
        if (videoId <= 0)
            throw new ArgumentOutOfRangeException(nameof(videoId));
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Int64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt64(out var result)
            ? result
            : 0;

    private static bool Boolean(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return false;
        return value.ValueKind == JsonValueKind.True ||
               value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric) && numeric != 0;
    }
}
