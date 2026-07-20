using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Публикация клипов (VK Клипы) — по флоу веб-клиента:
/// shortVideo.create → загрузка на CDN → shortVideo.encodeProgress → shortVideo.edit → shortVideo.publish.
///
/// Параметры публикации (описание, приватность, дуэты, стена, сообщество, отложка)
/// задаются через <see cref="VkClipPublishOptions"/>.
///
/// Замечания:
///  • крупные клипы веб грузит чанками (4 канала); здесь — одиночный POST,
///    чего достаточно для роликов умеренного размера;
///  • выбор конкретного кадра обложки не реализован (кадр по умолчанию);
///  • VK требует минимальный размер файла 16 КБ.
/// </summary>
public sealed class VkClipsService
{
    private readonly VkClient _client;

    internal VkClipsService(VkClient client) => _client = client;

    /// <summary>Опубликовать клип из файла.</summary>
    public Task<VkClipResult> PublishFromFileAsync(
        string path, VkClipPublishOptions? options = null, CancellationToken cancellationToken = default)
        => PublishAsync(VkUploadSource.FromFile(path, "video/mp4"), options, cancellationToken);

    /// <summary>Опубликовать клип из байтов видео (вертикальное короткое видео).</summary>
    public Task<VkClipResult> PublishAsync(
        byte[] video, string fileName, VkClipPublishOptions? options = null, CancellationToken cancellationToken = default)
        => PublishAsync(
            VkUploadSource.FromBytes(video, fileName, "video/mp4"),
            options,
            cancellationToken);

    /// <summary>
    /// Потоково опубликовать клип из повторно открываемого источника. Файл не читается
    /// целиком в память; источник может быть открыт повторно при сетевом ретрае.
    /// </summary>
    public async Task<VkClipResult> PublishAsync(
        VkUploadSource video,
        VkClipPublishOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new VkClipPublishOptions();
        var session = await CreateUploadSessionAsync(video, options, cancellationToken).ConfigureAwait(false);
        session = await UploadAsync(session, video, cancellationToken).ConfigureAwait(false);
        return await CompletePublishAsync(session, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Зарезервировать неизменяемый provider id и получить подписанный URL загрузки.
    /// Сохраните возвращённую сессию до начала загрузки: повторное продолжение должно
    /// использовать её, а не снова вызывать shortVideo.create.
    /// </summary>
    public async Task<VkClipUploadSession> CreateUploadSessionAsync(
        VkUploadSource video,
        VkClipPublishOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateVideo(video);
        options ??= new VkClipPublishOptions();
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);

        long ownerId, videoId;
        string uploadUrl;
        using (var doc = await api.CallAsync("shortVideo.create", new Dictionary<string, string>
        {
            ["file_size"] = video.Length.ToString(),
            ["group_id"] = options.GroupId?.ToString() ?? "",
        }, cancellationToken).ConfigureAwait(false))
        {
            var r = VkWebApi.GetResponseOrThrow(doc, "shortVideo.create");
            ownerId = r.TryGetProperty("owner_id", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
            videoId = r.TryGetProperty("video_id", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
            uploadUrl = r.TryGetProperty("upload_url", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(uploadUrl) || ownerId == 0 || videoId == 0)
                throw new VkClientException("shortVideo.create не вернул upload_url/owner_id/video_id.");
        }

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkClipUploadSession
        {
            OwnerId = ownerId,
            VideoId = videoId,
            UploadUrl = uploadUrl,
            Stage = VkClipUploadStage.Created
        };
    }

    /// <summary>
    /// Загрузить файл в ранее зарезервированный Clip. Временные ошибки повторяют только
    /// загрузку в тот же URL и никогда не создают новый provider id.
    /// </summary>
    public async Task<VkClipUploadSession> UploadAsync(
        VkClipUploadSession session,
        VkUploadSource video,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session, VkClipUploadStage.Created);
        ValidateVideo(video);
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        var videoHash = await VkUploadRetry.ExecuteAsync(async () =>
        {
            using var up = await api.UploadFileAsync(session.UploadUrl, "video_file", video, cancellationToken)
                .ConfigureAwait(false);
            if (up.RootElement.TryGetProperty("error", out _))
                throw new VkClientException($"CDN отклонил клип: {up.RootElement.GetRawText()}");
            return up.RootElement.TryGetProperty("video_hash", out var vh) ? vh.GetString() ?? "" : "";
        }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(videoHash))
            throw new VkClientException("CDN принял запрос, но не вернул video_hash.");

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return session with { VideoHash = videoHash, Stage = VkClipUploadStage.Uploaded };
    }

    /// <summary>
    /// Дождаться обработки, применить метаданные и опубликовать ранее загруженный Clip.
    /// Повтор использует тот же provider id и не загружает медиа заново.
    /// </summary>
    public async Task<VkClipResult> CompletePublishAsync(
        VkClipUploadSession session,
        VkClipPublishOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session, VkClipUploadStage.Uploaded);
        options ??= new VkClipPublishOptions();
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);

        await WaitEncodedAsync(
            api,
            session.VideoId,
            session.OwnerId,
            session.VideoHash!,
            cancellationToken).ConfigureAwait(false);

        var editParams = new Dictionary<string, string>
        {
            ["video_id"] = session.VideoId.ToString(),
            ["owner_id"] = session.OwnerId.ToString(),
            ["privacy_view"] = VkClipPublishOptions.Privacy(options.View),
            ["privacy_comment"] = VkClipPublishOptions.Privacy(options.Comment),
            ["can_make_duet"] = options.AllowDuets ? "1" : "0",
        };
        if (!string.IsNullOrEmpty(options.Description))
            editParams["description"] = options.Description;
        using (await api.CallAsync("shortVideo.edit", editParams, cancellationToken).ConfigureAwait(false)) { }

        using (var pub = await api.CallAsync("shortVideo.publish", new Dictionary<string, string>
        {
            ["video_id"] = session.VideoId.ToString(),
            ["owner_id"] = session.OwnerId.ToString(),
            ["wallpost"] = options.PostToWall ? "1" : "0",
            ["publish_date"] = (options.PublishAt?.ToUnixTimeSeconds() ?? 0).ToString(),
            ["license_agree"] = "1",
            ["ref"] = "clips_viewer",
        }, cancellationToken).ConfigureAwait(false))
        {
            VkWebApi.GetResponseOrThrow(pub, "shortVideo.publish");
        }

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkClipResult { OwnerId = session.OwnerId, VideoId = session.VideoId };
    }

    /// <summary>
    /// Изменить описание уже созданного/опубликованного клипа (video{owner}_{id}).
    /// Обновляет только описание — приватность и прочие настройки не сбрасываются.
    /// </summary>
    /// <returns>Применённое описание (по ответу API).</returns>
    public async Task<string> EditDescriptionAsync(long ownerId, long videoId, string description, CancellationToken cancellationToken = default)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync("shortVideo.edit", new Dictionary<string, string>
        {
            ["video_id"] = videoId.ToString(),
            ["owner_id"] = ownerId.ToString(),
            ["description"] = description ?? "",
        }, cancellationToken).ConfigureAwait(false);

        var r = VkWebApi.GetResponseOrThrow(doc, "shortVideo.edit");
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);

        // Ответ shortVideo.edit отражает применённое описание.
        return r.TryGetProperty("video", out var v) && v.TryGetProperty("description", out var d)
            ? d.GetString() ?? description ?? ""
            : description ?? "";
    }

    /// <summary>Изменить описание клипа, полученного из потоковой перегрузки PublishAsync.</summary>
    public Task<string> EditDescriptionAsync(VkClipResult clip, string description, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return EditDescriptionAsync(clip.OwnerId, clip.VideoId, description, cancellationToken);
    }

    /// <summary>Проверить, завершил ли VK обработку опубликованного клипа.</summary>
    public async Task<VkVideoProcessingResult> GetProcessingStatusAsync(
        long ownerId,
        long videoId,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == 0)
            throw new ArgumentOutOfRangeException(nameof(ownerId));
        if (videoId <= 0)
            throw new ArgumentOutOfRangeException(nameof(videoId));

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync("video.get", new Dictionary<string, string>
        {
            ["videos"] = $"{ownerId}_{videoId}",
            ["count"] = "1",
        }, cancellationToken).ConfigureAwait(false);
        var response = VkWebApi.GetResponseOrThrow(doc, "video.get");
        var item = response.TryGetProperty("items", out var items) &&
                   items.ValueKind == JsonValueKind.Array &&
                   items.GetArrayLength() > 0
            ? items[0]
            : default;

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new VkVideoProcessingResult
            {
                OwnerId = ownerId,
                VideoId = videoId,
                State = VkVideoProcessingState.NotFound
            };
        }

        var processing = item.TryGetProperty("processing", out var state) &&
                         (state.ValueKind == JsonValueKind.True ||
                          state.ValueKind == JsonValueKind.Number && state.TryGetInt32(out var numeric) && numeric != 0);
        return new VkVideoProcessingResult
        {
            OwnerId = ownerId,
            VideoId = videoId,
            State = processing ? VkVideoProcessingState.Processing : VkVideoProcessingState.Ready
        };
    }

    /// <summary>Проверить обработку клипа, возвращённого <see cref="PublishAsync(VkUploadSource,VkClipPublishOptions?,CancellationToken)"/>.</summary>
    public Task<VkVideoProcessingResult> GetProcessingStatusAsync(
        VkClipResult clip,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return GetProcessingStatusAsync(clip.OwnerId, clip.VideoId, cancellationToken);
    }

    private static async Task WaitEncodedAsync(VkWebApi api, long videoId, long ownerId, string hash, CancellationToken ct)
    {
        // Опрос прогресса кодирования до ~90 секунд.
        for (var i = 0; i < 90; i++)
        {
            try
            {
                using var doc = await api.CallAsync("shortVideo.encodeProgress", new Dictionary<string, string>
                {
                    ["video_id"] = videoId.ToString(),
                    ["owner_id"] = ownerId.ToString(),
                    ["hash"] = hash,
                }, ct).ConfigureAwait(false);

                var r = VkWebApi.GetResponseOrThrow(doc, "shortVideo.encodeProgress");
                if (r.TryGetProperty("is_ready", out var ready) && ready.ValueKind == JsonValueKind.True)
                    return;
            }
            catch (VkClientException)
            {
                // best-effort: если прогресс опросить не удалось, пробуем опубликовать всё равно
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    private static void ValidateVideo(VkUploadSource video)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (video.Length < 16384)
            throw new ArgumentException("Клип слишком мал: VK требует минимум 16 КБ.", nameof(video));
    }

    private static void ValidateSession(VkClipUploadSession session, VkClipUploadStage expectedStage)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.OwnerId == 0 || session.VideoId <= 0 || string.IsNullOrWhiteSpace(session.UploadUrl))
            throw new ArgumentException("Сессия загрузки Клипа неполна.", nameof(session));
        if (session.Stage != expectedStage)
            throw new InvalidOperationException(
                $"Ожидался этап Клипа {expectedStage}, получен {session.Stage}.");
        if (expectedStage == VkClipUploadStage.Uploaded && string.IsNullOrWhiteSpace(session.VideoHash))
            throw new ArgumentException("Загруженная сессия Клипа не содержит video_hash.", nameof(session));
    }
}
