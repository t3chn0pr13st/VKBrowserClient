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
    /// Применить приватность и опубликовать черновик через приложение VK Видео.
    /// Пока файл обрабатывается, VK может принять edit, но не вернуть privacy_view:
    /// это промежуточное состояние, а не ошибка. Готовая запись без подтверждённой
    /// приватности отклоняется. Публикация всегда выполняется без записи на стене.
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
        if (!privacy.Accepted)
            throw new VkClientException(
                $"VK отклонил privacy_view={VkLiveStartOptions.Privacy(options.ViewPrivacy)} для видео {session.Reference}.");

        var accessKey = privacy.AccessKey ?? session.AccessKey;
        var status = await GetStatusAsync(
                session.OwnerId,
                session.VideoId,
                accessKey,
                cancellationToken)
            .ConfigureAwait(false);
        var confirmedPrivacy = privacy.Privacy ?? status.PrivacyView;
        if (confirmedPrivacy is not null &&
            !string.Equals(confirmedPrivacy, VkLiveStartOptions.Privacy(options.ViewPrivacy), StringComparison.OrdinalIgnoreCase))
        {
            throw new VkClientException(
                $"VK вернул privacy_view={confirmedPrivacy} вместо {VkLiveStartOptions.Privacy(options.ViewPrivacy)} для видео {session.Reference}.");
        }
        if (status.State == VkVideoProcessingState.Ready && confirmedPrivacy is null)
        {
            throw new VkClientException(
                $"VK принял видео {session.Reference}, но не подтвердил privacy_view={VkLiveStartOptions.Privacy(options.ViewPrivacy)} после обработки.");
        }

        status = WithDraftState(status, await GetDraftStateAsync(
                session.OwnerId,
                session.VideoId,
                cancellationToken)
            .ConfigureAwait(false));

        // video.save создаёт черновик. Официальный интерфейс VK Видео завершает
        // загрузку отдельным video.publish; одного video.edit недостаточно.
        // Сначала читаем is_draft, чтобы повтор после неопределённого ответа не
        // публиковал уже опубликованный объект вслепую.
        if (status.IsDraft == true)
        {
            accessKey = await PublishDraftAsync(
                    session.OwnerId,
                    session.VideoId,
                    options,
                    accessKey,
                    cancellationToken)
                .ConfigureAwait(false);
            status = await GetStatusAsync(
                    session.OwnerId,
                    session.VideoId,
                    accessKey,
                    cancellationToken)
                .ConfigureAwait(false);
            status = WithDraftState(status, await GetDraftStateAsync(
                    session.OwnerId,
                    session.VideoId,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        if (status.State == VkVideoProcessingState.Ready && confirmedPrivacy is null)
        {
            throw new VkClientException(
                $"VK принял видео {session.Reference}, но не подтвердил privacy_view={VkLiveStartOptions.Privacy(options.ViewPrivacy)} после обработки.");
        }

        return new VkVideoResult
        {
            OwnerId = status.OwnerId,
            VideoId = status.VideoId,
            AccessKey = status.AccessKey,
            State = status.State,
            PlayerUrl = status.PlayerUrl,
            PrivacyView = confirmedPrivacy,
            IsDraft = status.IsDraft,
        };
    }

    /// <summary>
    /// Опубликовать созданный <c>video.save</c> черновик без публикации на стене.
    /// Метод намеренно вызывается только после readback <c>is_draft=true</c>.
    /// </summary>
    private async Task<string?> PublishDraftAsync(
        long ownerId,
        long videoId,
        VkVideoUploadOptions options,
        string? accessKey,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["video_id"] = videoId.ToString(),
            ["privacy_video"] = VkLiveStartOptions.Privacy(options.ViewPrivacy),
            ["add_to_wall"] = "0",
        };
        if (!string.IsNullOrWhiteSpace(options.Name))
            parameters["title"] = options.Name;
        if (options.Description is not null)
            parameters["description"] = options.Description;

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var document = await api.CallVideoAsync("video.publish", parameters, cancellationToken)
            .ConfigureAwait(false);
        var response = VkWebApi.GetResponseOrThrow(document, "video.publish");
        var video = response.TryGetProperty("video", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
        var publishedId = Int64(video, "id");
        var publishedOwnerId = Int64(video, "owner_id");
        if (publishedId != videoId || publishedOwnerId != 0 && publishedOwnerId != ownerId)
        {
            throw new VkClientException(
                $"video.publish не подтвердил публикацию ожидаемого видео video{ownerId}_{videoId}.");
        }

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return String(video, "access_key") ?? accessKey;
    }

    private async Task<bool?> GetDraftStateAsync(
        long ownerId,
        long videoId,
        CancellationToken cancellationToken)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var document = await api.CallVideoAsync("video.getVideoForEdit", new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["video_id"] = videoId.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        var response = VkWebApi.GetResponseOrThrow(document, "video.getVideoForEdit");
        if (!response.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return null;

        // VK omits is_draft for an already published object. The same shape is
        // consumed by the official editor, where an absent field means false.
        return NullableBoolean(item, "is_draft") ?? false;
    }

    private static VkVideoResult WithDraftState(VkVideoResult status, bool? isDraft) => new()
    {
        OwnerId = status.OwnerId,
        VideoId = status.VideoId,
        AccessKey = status.AccessKey,
        State = isDraft == true ? VkVideoProcessingState.Processing : status.State,
        PlayerUrl = status.PlayerUrl,
        PrivacyView = status.PrivacyView,
        IsDraft = isDraft,
    };

    /// <summary>Проверить обработку записи по стабильному provider id.</summary>
    public async Task<VkVideoResult> GetStatusAsync(
        long ownerId,
        long videoId,
        string? accessKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);
        // Для управляющего токена запрашиваем объект как владелец, без access key.
        // Именно такой запрос VK Video использует в редакторе и только в нём
        // возвращает lifecycle-поля наподобие is_draft. Переданный ключ остаётся
        // fallback-значением результата и не теряется.
        var reference = $"{ownerId}_{videoId}";
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        // Только API приложения VK Видео возвращает полный lifecycle VOD,
        // включая is_draft. Обычный web API может скрыть это поле и тем самым
        // ошибочно представить неопубликованный черновик готовым видео.
        using var document = await api.CallVideoAsync("video.get", new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["videos"] = reference,
            ["count"] = "1",
            ["extended"] = "1",
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

        var isDraft = NullableBoolean(item, "is_draft");
        var processing = Boolean(item, "processing") || Boolean(item, "converting") || isDraft == true;
        return new VkVideoResult
        {
            OwnerId = ownerId,
            VideoId = videoId,
            AccessKey = String(item, "access_key") ?? accessKey,
            State = processing ? VkVideoProcessingState.Processing : VkVideoProcessingState.Ready,
            PlayerUrl = String(item, "player"),
            PrivacyView = ReadPrivacyView(item),
            IsDraft = isDraft,
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

    private static bool? NullableBoolean(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.False)
            return false;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric)
            ? numeric != 0
            : null;
    }

    private static string? ReadPrivacyView(JsonElement item)
    {
        if (!item.TryGetProperty("privacy_view", out var privacy))
            return null;
        return privacy.ValueKind switch
        {
            JsonValueKind.String => privacy.GetString(),
            JsonValueKind.Object => privacy.TryGetProperty("category", out var category)
                                    && category.ValueKind == JsonValueKind.String
                ? category.GetString()
                : null,
            JsonValueKind.Array => privacy.GetArrayLength() > 0 && privacy[0].ValueKind == JsonValueKind.String
                ? privacy[0].GetString()
                : null,
            _ => null,
        };
    }
}
