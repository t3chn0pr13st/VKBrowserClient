using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Typed lifecycle прямых трансляций VK Видео поверх официальных методов <c>video.*</c>:
/// startStreaming/stopStreaming, категории, edit/get/delete и загрузка обложки.
/// </summary>
public sealed class VkLiveService
{
    private const string ThumbnailUploadField = "file";
    private readonly VkClient _client;

    internal VkLiveService(VkClient client) => _client = client;

    /// <summary>
    /// Создать трансляцию или адресовать уже известный <see cref="VkLiveStartOptions.VideoId"/>.
    /// Сохраните OwnerId/VideoId из результата до следующей provider-операции.
    /// Если первый запрос завершился неоднозначной сетевой ошибкой до получения результата,
    /// его нельзя слепо повторять без reconciliation: у video.startStreaming нет idempotency key.
    /// </summary>
    public async Task<VkLiveStream> StartStreamingAsync(
        VkLiveStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var parameters = new Dictionary<string, string>
        {
            ["wallpost"] = Bool(options.PostToWall),
            ["privacy_view"] = VkLiveStartOptions.Privacy(options.ViewPrivacy),
            ["privacy_comment"] = VkLiveStartOptions.Privacy(options.CommentPrivacy),
            ["no_comments"] = Bool(options.DisableComments),
            ["publish"] = Bool(options.Publish),
        };
        AddOptional(parameters, "video_id", options.VideoId);
        AddOptional(parameters, "name", options.Name);
        AddOptional(parameters, "description", options.Description);
        AddOptional(parameters, "group_id", options.GroupId);
        AddOptional(parameters, "category_id", options.CategoryId);

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync("video.startStreaming", parameters, cancellationToken)
            .ConfigureAwait(false);
        var response = ResponseOrThrow(doc, "video.startStreaming");
        if (response.ValueKind != JsonValueKind.Object)
            throw new VkClientException("video.startStreaming вернул ответ неожиданного типа.");

        var ownerId = Int64(response, "owner_id");
        var videoId = Int64(response, "video_id");
        var accessKey = String(response, "access_key") ?? "";
        if (ownerId == 0 || videoId <= 0 ||
            !response.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Object)
        {
            throw new VkClientException(
                "video.startStreaming не вернул owner_id/video_id/stream; безопасно продолжить lifecycle нельзя.");
        }

        var ingestUrl = String(stream, "url");
        var ingestKey = String(stream, "key");
        if (string.IsNullOrWhiteSpace(ingestUrl) || string.IsNullOrWhiteSpace(ingestKey))
            throw new VkClientException("video.startStreaming не вернул URL и ключ входного потока.");

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkLiveStream
        {
            OwnerId = ownerId,
            VideoId = videoId,
            Name = String(response, "name") ?? options.Name ?? "",
            Description = String(response, "description") ?? options.Description ?? "",
            AccessKey = accessKey,
            Ingest = new VkLiveIngest
            {
                Url = ingestUrl,
                Key = ingestKey,
                OkmpUrl = String(stream, "okmp_url"),
                WebRtcUrl = String(stream, "webrtc_url"),
            },
            PostId = NullablePositiveInt64(response, "post_id"),
        };
    }

    /// <summary>Получить древовидный список live-категорий VK.</summary>
    public async Task<IReadOnlyList<VkLiveCategory>> GetCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync("video.liveGetCategories", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var response = ResponseOrThrow(doc, "video.liveGetCategories");
        if (response.ValueKind != JsonValueKind.Array)
            throw new VkClientException("video.liveGetCategories вернул не массив категорий.");

        var categories = response.EnumerateArray().Select(ParseCategory).ToArray();
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return categories;
    }

    /// <summary>Остановить трансляцию по стабильной provider-ссылке.</summary>
    public Task<VkLiveStopResult> StopStreamingAsync(
        VkLiveReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return StopStreamingAsync(reference.OwnerId, reference.VideoId, cancellationToken);
    }

    /// <summary>Остановить трансляцию по owner_id/video_id.</summary>
    public async Task<VkLiveStopResult> StopStreamingAsync(
        long ownerId,
        long videoId,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);
        // VK Видео завершает эфир из producer UI именно этим запросом под
        // приложением VK Видео:
        // POST https://api.vkvideo.ru/method/video.stopStreaming
        //   group_id=<positive community id>&video_id=<id>&extended=0
        // Не заменяйте CallVideoAsync на обычный CallAsync: producer UI использует
        // video app token. Для уже приостановленного live-SDK слота сам метод может
        // вернуть API error 10, хотя последующая provider-сверка подтвердит Completed;
        // вызывающий код не должен по этому результату пересоздавать или удалять слот.
        var parameters = new Dictionary<string, string>
        {
            ["video_id"] = videoId.ToString(),
            ["extended"] = "0",
        };
        if (ownerId < 0)
            parameters["group_id"] = checked(-ownerId).ToString();

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallVideoAsync("video.stopStreaming", parameters, cancellationToken)
            .ConfigureAwait(false);
        var response = ResponseOrThrow(doc, "video.stopStreaming");
        if (response.ValueKind != JsonValueKind.Object)
            throw new VkClientException("video.stopStreaming вернул ответ неожиданного типа.");

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkLiveStopResult
        {
            UniqueViewers = Math.Max(0, Int64(response, "unique_viewers")),
        };
    }

    /// <summary>Изменить метаданные/приватность существующей трансляции или записи.</summary>
    public Task<VkLiveUpdateResult> UpdateAsync(
        VkLiveReference reference,
        VkLiveUpdateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return UpdateAsync(reference.OwnerId, reference.VideoId, options, cancellationToken);
    }

    /// <summary>Изменить метаданные/приватность по owner_id/video_id.</summary>
    public async Task<VkLiveUpdateResult> UpdateAsync(
        long ownerId,
        long videoId,
        VkLiveUpdateOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var parameters = new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["video_id"] = videoId.ToString(),
        };
        AddOptional(parameters, "name", options.Name);
        AddOptional(parameters, "desc", options.Description);
        if (options.ViewPrivacy is { } view)
            parameters["privacy_view"] = VkLiveStartOptions.Privacy(view);
        if (options.CommentPrivacy is { } comment)
            parameters["privacy_comment"] = VkLiveStartOptions.Privacy(comment);
        if (options.DisableComments is { } disableComments)
            parameters["no_comments"] = Bool(disableComments);
        if (options.Repeat is { } repeat)
            parameters["repeat"] = Bool(repeat);

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync("video.edit", parameters, cancellationToken).ConfigureAwait(false);
        var response = ResponseOrThrow(doc, "video.edit");
        var success = response.ValueKind switch
        {
            JsonValueKind.Object => Boolean(response, "success"),
            JsonValueKind.Number => response.TryGetInt64(out var value) && value != 0,
            JsonValueKind.True => true,
            _ => false,
        };

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkLiveUpdateResult
        {
            Success = success,
            AccessKey = response.ValueKind == JsonValueKind.Object ? String(response, "access_key") : null,
        };
    }

    /// <summary>
    /// Задать приватность самой видеозаписи через приложение «VK Видео».
    ///
    /// Это отдельная настройка от приватности эфира: у видео сообщества её меняет только
    /// этот путь. Тот же <c>video.edit</c> под токеном мессенджера отвечает успехом и
    /// оставляет запись открытой для всех — проверено на живом сообществе 19.08.2026.
    /// </summary>
    public async Task<VkVideoPrivacyResult> SetVideoPrivacyAsync(
        long ownerId,
        long videoId,
        VkLivePrivacy view,
        string? name = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);

        var parameters = new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["video_id"] = videoId.ToString(),
            ["privacy_view"] = VkLiveStartOptions.Privacy(view),
        };
        AddOptional(parameters, "name", name);
        AddOptional(parameters, "desc", description);

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using (var doc = await api.CallVideoAsync("video.edit", parameters, cancellationToken).ConfigureAwait(false))
        {
            var response = ResponseOrThrow(doc, "video.edit");
            var accepted = response.ValueKind switch
            {
                JsonValueKind.Object => Boolean(response, "success"),
                JsonValueKind.Number => response.TryGetInt64(out var value) && value != 0,
                JsonValueKind.True => true,
                _ => false,
            };
            if (!accepted)
            {
                await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
                return new VkVideoPrivacyResult { Accepted = false };
            }
        }

        var confirmed = await GetVideoPrivacyAsync(ownerId, videoId, cancellationToken).ConfigureAwait(false);
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkVideoPrivacyResult { Accepted = true, Privacy = confirmed };
    }

    /// <summary>
    /// Прочитать приватность видео через приложение «VK Видео». Возвращает
    /// <see langword="null"/>, когда VK не отдаёт поле: это «неизвестно», а не «открыто».
    /// </summary>
    public async Task<string?> GetVideoPrivacyAsync(
        long ownerId,
        long videoId,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallVideoAsync(
            "video.get",
            new Dictionary<string, string>
            {
                ["owner_id"] = ownerId.ToString(),
                ["videos"] = $"{ownerId}_{videoId}",
                ["count"] = "1",
            },
            cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("response", out var response)
            || !response.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() == 0)
        {
            return null;
        }

        return ReadPrivacyView(items[0]);
    }

    /// <summary>
    /// VK отдаёт privacy_view то строкой, то объектом с <c>category</c>, то списком.
    /// Нераспознанная форма — это «неизвестно».
    /// </summary>
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

    /// <summary>
    /// Получить состояние по стабильному owner_id/video_id. Передавайте сохранённый
    /// access_key для непубличной трансляции; NotFound в таком случае означает также
    /// «недоступно с этим ключом».
    /// </summary>
    public Task<VkLiveStatus> GetStatusAsync(
        VkLiveReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return GetStatusAsync(reference.OwnerId, reference.VideoId, reference.AccessKey, cancellationToken);
    }

    /// <summary>Получить состояние публичного объекта по owner_id/video_id.</summary>
    public Task<VkLiveStatus> GetStatusAsync(
        long ownerId,
        long videoId,
        CancellationToken cancellationToken) =>
        GetStatusAsync(ownerId, videoId, accessKey: null, cancellationToken);

    /// <summary>Получить состояние по owner_id/video_id и необязательному access_key.</summary>
    public async Task<VkLiveStatus> GetStatusAsync(
        long ownerId,
        long videoId,
        string? accessKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);
        var reference = new VkLiveReference
        {
            OwnerId = ownerId,
            VideoId = videoId,
            AccessKey = accessKey,
        };
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync("video.get", new Dictionary<string, string>
        {
            ["videos"] = reference.ApiReference,
            ["count"] = "1",
        }, cancellationToken).ConfigureAwait(false);
        var response = ResponseOrThrow(doc, "video.get");
        var item = FirstVideo(response);

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return item.ValueKind == JsonValueKind.Object
            ? ParseStatus(item, reference)
            : NotFound(reference);
    }

    /// <summary>Удалить трансляцию/готовую запись. Возвращает false, если VK не подтвердил операцию.</summary>
    public Task<bool> DeleteAsync(VkLiveReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return DeleteAsync(reference.OwnerId, reference.VideoId, cancellationToken);
    }

    /// <summary>Удалить трансляцию/готовую запись по owner_id/video_id.</summary>
    public async Task<bool> DeleteAsync(
        long ownerId,
        long videoId,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync("video.delete", new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["video_id"] = videoId.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        var response = ResponseOrThrow(doc, "video.delete");
        var success = response.ValueKind switch
        {
            JsonValueKind.Number => response.TryGetInt64(out var value) && value != 0,
            JsonValueKind.True => true,
            JsonValueKind.Object => Boolean(response, "success"),
            _ => false,
        };
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return success;
    }

    /// <summary>Получить подписанный upload URL для обложки и связать его с video_id.</summary>
    public async Task<VkLiveThumbnailUploadSession> CreateThumbnailUploadSessionAsync(
        long ownerId,
        long videoId,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync("video.getThumbUploadUrl", new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        var response = ResponseOrThrow(doc, "video.getThumbUploadUrl");
        var uploadUrl = response.ValueKind == JsonValueKind.Object ? String(response, "upload_url") : null;
        if (string.IsNullOrWhiteSpace(uploadUrl))
            throw new VkClientException("video.getThumbUploadUrl не вернул upload_url.");

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkLiveThumbnailUploadSession
        {
            OwnerId = ownerId,
            VideoId = videoId,
            UploadUrl = uploadUrl,
        };
    }

    /// <summary>
    /// Загрузить обложку в ранее полученный URL. Источник открывается заново при
    /// временном upload-сбое; новый upload URL и новый video_id не создаются.
    /// </summary>
    public async Task<VkLiveThumbnailUpload> UploadThumbnailAsync(
        VkLiveThumbnailUploadSession session,
        VkUploadSource image,
        CancellationToken cancellationToken = default)
    {
        ValidateThumbnailSession(session);
        ValidateImage(image);
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);

        var uploaded = await VkUploadRetry.ExecuteAsync(async () =>
        {
            using var doc = await api.UploadFileAsync(
                    session.UploadUrl,
                    ThumbnailUploadField,
                    image,
                    cancellationToken)
                .ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new VkClientException(
                    $"Upload-сервер обложки вернул неожиданный ответ: {VkSafeErrorDetails.Describe(root)}");
            if (root.TryGetProperty("error", out _))
                throw new VkClientException(
                    $"Upload-сервер обложки отклонил файл: {VkSafeErrorDetails.Describe(root)}");

            // video.saveUploadedThumb expects the complete upload-server JSON
            // serialized as the opaque thumb_json value. The upload response
            // itself does not contain a nested thumb_json field.
            var thumbJson = root.GetRawText();
            if (string.IsNullOrWhiteSpace(thumbJson) || thumbJson == "{}")
                throw new VkClientException("Upload-сервер обложки вернул пустой JSON-ответ.");

            return new VkLiveThumbnailUpload
            {
                OwnerId = session.OwnerId,
                VideoId = session.VideoId,
                ThumbJson = thumbJson,
                ThumbSize = String(root, "thumb_size"),
                RandomTag = String(root, "random_tag"),
            };
        }, cancellationToken).ConfigureAwait(false);

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return uploaded;
    }

    /// <summary>Сохранить уже загруженную обложку и установить её для video_id.</summary>
    public async Task<VkLiveThumbnailResult> SaveThumbnailAsync(
        VkLiveThumbnailUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ValidateReference(upload.OwnerId, upload.VideoId);
        if (string.IsNullOrWhiteSpace(upload.ThumbJson))
            throw new ArgumentException("ThumbJson не может быть пустым.", nameof(upload));

        var parameters = new Dictionary<string, string>
        {
            ["owner_id"] = upload.OwnerId.ToString(),
            ["video_id"] = upload.VideoId.ToString(),
            ["thumb_json"] = upload.ThumbJson,
            ["set_thumb"] = "1",
        };
        AddOptional(parameters, "thumb_size", upload.ThumbSize);
        AddOptional(parameters, "random_tag", upload.RandomTag);

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await api.CallAsync("video.saveUploadedThumb", parameters, cancellationToken)
            .ConfigureAwait(false);
        var response = ResponseOrThrow(doc, "video.saveUploadedThumb");
        if (response.ValueKind != JsonValueKind.Object)
            throw new VkClientException("video.saveUploadedThumb вернул ответ неожиданного типа.");

        var photoId = Int64(response, "photo_id");
        var hash = String(response, "photo_hash");
        if (photoId <= 0 || string.IsNullOrWhiteSpace(hash))
            throw new VkClientException("video.saveUploadedThumb не вернул photo_id/photo_hash.");

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkLiveThumbnailResult
        {
            PhotoId = photoId,
            PhotoOwnerId = NullableInt64(response, "photo_owner_id"),
            PhotoHash = hash,
            Images = ParseImages(response),
        };
    }

    /// <summary>Удобный полный флоу get upload URL → upload → save для одной обложки.</summary>
    public async Task<VkLiveThumbnailResult> SetThumbnailAsync(
        long ownerId,
        long videoId,
        VkUploadSource image,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(ownerId, videoId);
        ValidateImage(image);
        var session = await CreateThumbnailUploadSessionAsync(ownerId, videoId, cancellationToken)
            .ConfigureAwait(false);
        var uploaded = await UploadThumbnailAsync(session, image, cancellationToken).ConfigureAwait(false);
        return await SaveThumbnailAsync(uploaded, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Удобный полный флоу установки обложки по provider-ссылке.</summary>
    public Task<VkLiveThumbnailResult> SetThumbnailAsync(
        VkLiveReference reference,
        VkUploadSource image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return SetThumbnailAsync(reference.OwnerId, reference.VideoId, image, cancellationToken);
    }

    private static VkLiveCategory ParseCategory(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new VkClientException("video.liveGetCategories содержит категорию неожиданного типа.");
        var id = checked((int)Int64(value, "id"));
        var label = String(value, "label");
        if (id < 0 || string.IsNullOrWhiteSpace(label))
            throw new VkClientException("video.liveGetCategories содержит категорию без id/label.");

        IReadOnlyList<VkLiveCategory> children = [];
        if (value.TryGetProperty("sublist", out var sublist))
        {
            if (sublist.ValueKind != JsonValueKind.Array)
                throw new VkClientException("Поле sublist live-категории имеет неожиданный тип.");
            children = sublist.EnumerateArray().Select(ParseCategory).ToArray();
        }

        return new VkLiveCategory { Id = id, Label = label, Children = children };
    }

    private static VkLiveStatus ParseStatus(JsonElement item, VkLiveReference fallback)
    {
        var ownerId = Int64(item, "owner_id");
        var videoId = Int64(item, "id");
        if (ownerId == 0)
            ownerId = fallback.OwnerId;
        if (videoId <= 0)
            videoId = fallback.VideoId;

        var upcoming = Boolean(item, "upcoming");
        var live = Boolean(item, "live");
        var processing = Boolean(item, "processing") || Boolean(item, "converting");
        var type = String(item, "type");
        // Флаг live говорит лишь то, что объект — трансляция, а не запись «живым
        // сейчас». Фазу несёт live_status, и без него прошедший эфир годами
        // оставался бы «в эфире». Неизвестное значение отдаём старой эвристике:
        // так ответ без live_status разбирается ровно как раньше.
        var liveStatus = String(item, "live_status");
        var state = liveStatus switch
        {
            "started" => VkLiveStatusState.Live,
            "waiting" or "upcoming" => VkLiveStatusState.Upcoming,
            // postlive — эфир окончен и VK готовит запись; снято с живого ответа 20.08.2026.
            "finished" or "postlive" => processing ? VkLiveStatusState.Processing : VkLiveStatusState.Ready,
            "failed" => VkLiveStatusState.Unknown,
            // Значение есть, но незнакомое: флаг live тут не помощник — он говорит
            // лишь «объект является трансляцией». Опираемся на остальные признаки,
            // иначе любая новая фаза VK снова превратится в вечное «в эфире».
            { Length: > 0 } => processing
                ? VkLiveStatusState.Processing
                : !string.IsNullOrWhiteSpace(type)
                    ? VkLiveStatusState.Ready
                    : VkLiveStatusState.Unknown,
            _ => upcoming
                ? VkLiveStatusState.Upcoming
                : live
                    ? VkLiveStatusState.Live
                    : processing
                        ? VkLiveStatusState.Processing
                        : !string.IsNullOrWhiteSpace(type)
                            ? VkLiveStatusState.Ready
                            : VkLiveStatusState.Unknown,
        };

        DateTimeOffset? scheduledAt = null;
        var unix = Int64(item, "live_start_time");
        if (unix > 0)
        {
            try { scheduledAt = DateTimeOffset.FromUnixTimeSeconds(unix); }
            catch (ArgumentOutOfRangeException) { }
        }

        var privacyKnown = HasBoolean(item, "is_private");
        var currentViewers = NullableInt64(item, "spectators");
        var totalViews = NullableInt64(item, "views");
        return new VkLiveStatus
        {
            OwnerId = ownerId,
            VideoId = videoId,
            State = state,
            ProviderStatus = string.IsNullOrWhiteSpace(liveStatus) ? null : liveStatus,
            Title = String(item, "title"),
            Description = String(item, "description"),
            AccessKey = String(item, "access_key") ??
                        ExtractLinkAccessKey(String(item, "vk_live_video_id")) ??
                        fallback.AccessKey,
            PlayerUrl = String(item, "player"),
            IsPrivate = Boolean(item, "is_private"),
            PrivacyKnown = privacyKnown,
            CanEdit = Boolean(item, "can_edit"),
            CanDelete = Boolean(item, "can_delete"),
            CurrentViewers = currentViewers is { } current ? Math.Max(0, current) : null,
            TotalViews = totalViews is { } total ? Math.Max(0, total) : null,
            Spectators = Math.Max(0, currentViewers ?? 0),
            ScheduledStartAt = scheduledAt,
            VideoType = type,
            Images = ParseImages(item),
        };
    }

    private static VkLiveStatus NotFound(VkLiveReference reference) => new()
    {
        OwnerId = reference.OwnerId,
        VideoId = reference.VideoId,
        AccessKey = reference.AccessKey,
        State = VkLiveStatusState.NotFound,
    };

    private static string? ExtractLinkAccessKey(string? compositeVideoId)
    {
        if (string.IsNullOrWhiteSpace(compositeVideoId)) return null;
        var marker = compositeVideoId.IndexOf("_ln-", StringComparison.Ordinal);
        if (marker < 0) return null;
        var value = compositeVideoId[(marker + 1)..];
        if (value.Length <= 3 || value.Any(ch =>
                !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
            return null;
        return value;
    }

    private static JsonElement FirstVideo(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            return items.GetArrayLength() > 0 ? items[0] : default;
        }

        if (response.ValueKind == JsonValueKind.Array)
            return response.GetArrayLength() > 0 ? response[0] : default;

        throw new VkClientException("video.get вернул ответ неожиданного типа.");
    }

    private static IReadOnlyList<VkLiveImage> ParseImages(JsonElement container)
    {
        if (!container.TryGetProperty("image", out var images) || images.ValueKind != JsonValueKind.Array)
            return [];

        return images.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(String(x, "url")))
            .Select(x => new VkLiveImage
            {
                Url = String(x, "url")!,
                Width = checked((int)Math.Clamp(Int64(x, "width"), 0, int.MaxValue)),
                Height = checked((int)Math.Clamp(Int64(x, "height"), 0, int.MaxValue)),
                Size = String(x, "size"),
            })
            .ToArray();
    }

    private static void ValidateReference(long ownerId, long videoId)
    {
        if (ownerId == 0)
            throw new ArgumentOutOfRangeException(nameof(ownerId), "OwnerId не может быть нулём.");
        if (videoId <= 0)
            throw new ArgumentOutOfRangeException(nameof(videoId), "VideoId должен быть положительным.");
    }

    private static void ValidateThumbnailSession(VkLiveThumbnailUploadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateReference(session.OwnerId, session.VideoId);
        if (string.IsNullOrWhiteSpace(session.UploadUrl) ||
            !Uri.TryCreate(session.UploadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Сессия обложки не содержит корректный upload URL.", nameof(session));
        }
    }

    private static void ValidateImage(VkUploadSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length <= 0)
            throw new ArgumentException("Файл обложки пуст.", nameof(image));
        if (!image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Обложка должна иметь MIME-тип image/*.", nameof(image));
    }

    private static void AddOptional(IDictionary<string, string> target, string key, string? value)
    {
        if (value is not null)
            target[key] = value;
    }

    private static void AddOptional(IDictionary<string, string> target, string key, long? value)
    {
        if (value.HasValue)
            target[key] = value.Value.ToString();
    }

    private static JsonElement ResponseOrThrow(JsonDocument document, string method)
    {
        if (!document.RootElement.TryGetProperty("response", out var response))
        {
            // Не включаем raw JSON: live-ответы могут содержать access_key и stream.key.
            throw new VkClientException($"Ответ '{method}' не содержит поля 'response'.");
        }

        return response;
    }

    private static string Bool(bool value) => value ? "1" : "0";

    private static string? String(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private static string? JsonString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item))
            return null;
        return item.ValueKind switch
        {
            JsonValueKind.String => item.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => item.GetRawText(),
            _ => null,
        };
    }

    private static long Int64(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.TryGetInt64(out var number) ? number : 0;

    private static long? NullableInt64(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.TryGetInt64(out var number) ? number : null;

    private static long? NullablePositiveInt64(JsonElement value, string property)
    {
        var number = NullableInt64(value, property);
        return number is > 0 ? number : null;
    }

    private static bool Boolean(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item))
            return false;
        return item.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => item.TryGetInt64(out var number) && number != 0,
            JsonValueKind.String => item.GetString() is "1" or "true",
            _ => false,
        };
    }

    private static bool HasBoolean(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)) return false;
        return item.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => true,
            JsonValueKind.Number => item.TryGetInt64(out _),
            JsonValueKind.String => item.GetString() is "0" or "1" or "true" or "false",
            _ => false,
        };
    }
}
