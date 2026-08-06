using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Загрузка медиа тем же трёхшаговым способом, что и веб-клиент:
/// get*UploadServer / *.save → POST файла на подписанный URL → *.save.
/// Возвращает строку-вложение (photo…/doc…/video…) для messages.send / wall.post.
/// Имена полей формы проверены на живых серверах: photo, file, video_file.
/// Загрузка на pu.vk.ru изредка отдаёт 3xx/405 на отдельных серверах, поэтому
/// шаг «получить сервер + залить файл» повторяется с новым URL (как retry_count в вебе).
/// </summary>
internal sealed class VkMediaUploader(VkWebApi api)
{
    // --- Фото -----------------------------------------------------------------

    public Task<string> UploadPhotoAsync(
        long? peerId,
        long? communityId,
        VkUploadSource source,
        CancellationToken ct)
    {
        if (peerId is long peer)
        {
            return UploadPhotoCoreAsync(
                "photos.getMessagesUploadServer",
                new Dictionary<string, string> { ["peer_id"] = peer.ToString() },
                "photos.saveMessagesPhoto",
                new Dictionary<string, string>(),
                source,
                ct);
        }

        var groupParameters = communityId is long group
            ? new Dictionary<string, string> { ["group_id"] = group.ToString() }
            : new Dictionary<string, string>();
        return UploadPhotoCoreAsync(
            "photos.getWallUploadServer",
            new Dictionary<string, string>(groupParameters),
            "photos.saveWallPhoto",
            groupParameters,
            source,
            ct);
    }

    private async Task<string> UploadPhotoCoreAsync(
        string getServer,
        Dictionary<string, string> serverParams,
        string saveMethod,
        Dictionary<string, string> saveParams,
        VkUploadSource source,
        CancellationToken ct)
    {
        var uploaded = await VkUploadRetry.ExecuteAsync(async () =>
        {
            var uploadUrl = await GetUploadUrlAsync(getServer, serverParams, ct).ConfigureAwait(false);
            return await api.UploadPhotoAsync(uploadUrl, source, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var parameters = new Dictionary<string, string>(saveParams)
        {
            ["photo"] = uploaded.Photo,
            ["server"] = uploaded.Server.ToString(),
            ["hash"] = uploaded.Hash,
        };
        using var saved = await api.CallAsync(saveMethod, parameters, ct).ConfigureAwait(false);

        var response = VkWebApi.GetResponseOrThrow(saved, saveMethod);
        if (response.ValueKind == JsonValueKind.Array)
            foreach (var el in response.EnumerateArray())
                return BuildRef("photo", el);
        throw new VkClientException($"{saveMethod} не вернул фото.");
    }

    // --- Документы (файлы, GIF, аудиосообщения) -------------------------------

    public async Task<string> UploadDocumentAsync(
        long? peerId,
        long? communityId,
        VkUploadSource source,
        VkDocType type,
        CancellationToken ct)
    {
        var typeStr = type switch
        {
            VkDocType.AudioMessage => "audio_message",
            VkDocType.Graffiti => "graffiti",
            _ => "doc",
        };

        string getServer;
        Dictionary<string, string> serverParams;
        if (peerId is long peer)
        {
            getServer = "docs.getMessagesUploadServer";
            serverParams = new Dictionary<string, string> { ["peer_id"] = peer.ToString(), ["type"] = typeStr };
        }
        else
        {
            getServer = "docs.getWallUploadServer";
            serverParams = communityId is long group
                ? new Dictionary<string, string> { ["group_id"] = group.ToString() }
                : new Dictionary<string, string>();
        }

        // Сервер документов принимает файл в поле «file» и возвращает {file: "<token>"}.
        var fileToken = await VkUploadRetry.ExecuteAsync(async () =>
        {
            var uploadUrl = await GetUploadUrlAsync(getServer, serverParams, ct).ConfigureAwait(false);
            using var up = await api.UploadFileAsync(uploadUrl, "file", source, ct).ConfigureAwait(false);
            var token = up.RootElement.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(token))
                throw new VkClientException(
                    $"Сервер документов не принял файл: {VkSafeErrorDetails.Describe(up.RootElement)}");
            return token;
        }, ct).ConfigureAwait(false);

        using var saved = await api.CallAsync("docs.save",
            new Dictionary<string, string> { ["file"] = fileToken, ["title"] = source.FileName }, ct).ConfigureAwait(false);

        // Ответ: {type: "doc"|"audio_message"|…, <type>: {id, owner_id, access_key?}}.
        var response = VkWebApi.GetResponseOrThrow(saved, "docs.save");
        var savedType = response.TryGetProperty("type", out var t) ? t.GetString() ?? "doc" : "doc";
        if (response.TryGetProperty(savedType, out var obj) || response.TryGetProperty("doc", out obj))
            return BuildRef("doc", obj);
        throw new VkClientException("docs.save не вернул документ.");
    }

    // --- Видео (в т.ч. клипы) -------------------------------------------------

    public async Task<string> UploadVideoAsync(
        long? communityId,
        VkUploadSource source,
        string? name,
        string? description,
        CancellationToken ct)
    {
        var saveParams = new Dictionary<string, string>
        {
            ["name"] = string.IsNullOrEmpty(name) ? source.FileName : name,
            ["is_private"] = "0",
            ["wallpost"] = "0",
        };
        if (communityId is long group)
            saveParams["group_id"] = group.ToString();
        if (!string.IsNullOrEmpty(description))
            saveParams["description"] = description;

        // Шаг 1: video.save отдаёт upload_url (на ovu.mycdn.me) и идентификаторы будущего видео.
        string uploadUrl;
        long ownerId, videoId;
        string? accessKey;
        using (var saved = await api.CallAsync("video.save", saveParams, ct).ConfigureAwait(false))
        {
            var r = VkWebApi.GetResponseOrThrow(saved, "video.save");
            uploadUrl = r.TryGetProperty("upload_url", out var u) ? u.GetString() ?? "" : "";
            ownerId = r.TryGetProperty("owner_id", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
            videoId = r.TryGetProperty("video_id", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
            accessKey = r.TryGetProperty("access_key", out var ak) ? ak.GetString() : null;
            if (string.IsNullOrEmpty(uploadUrl) || videoId == 0)
                throw new VkClientException("video.save не вернул upload_url/video_id.");
        }

        // Шаг 2: POST файла в поле «video_file» на CDN (повтор на том же URL, чтобы не плодить пустые видео).
        // Видео обрабатывается асинхронно, но ссылка-вложение уже валидна.
        await VkUploadRetry.ExecuteAsync(async () =>
        {
            using var up = await api.UploadFileAsync(uploadUrl, "video_file", source, ct).ConfigureAwait(false);
            if (up.RootElement.TryGetProperty("error", out _))
                throw new VkClientException(
                    $"CDN отклонил видео: {VkSafeErrorDetails.Describe(up.RootElement)}");
            return true;
        }, ct).ConfigureAwait(false);

        return string.IsNullOrEmpty(accessKey) ? $"video{ownerId}_{videoId}" : $"video{ownerId}_{videoId}_{accessKey}";
    }

    // --- Общее ----------------------------------------------------------------

    private async Task<string> GetUploadUrlAsync(string method, Dictionary<string, string> parameters, CancellationToken ct)
    {
        using var doc = await api.CallAsync(method, parameters, ct).ConfigureAwait(false);
        var resp = VkWebApi.GetResponseOrThrow(doc, method);
        var url = resp.TryGetProperty("upload_url", out var u) ? u.GetString() : null;
        return string.IsNullOrEmpty(url) ? throw new VkClientException($"{method} не вернул upload_url.") : url;
    }

    private static string BuildRef(string prefix, JsonElement obj)
    {
        var ownerId = obj.TryGetProperty("owner_id", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
        var id = obj.TryGetProperty("id", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
        var accessKey = obj.TryGetProperty("access_key", out var ak) ? ak.GetString() : null;
        if (id == 0)
            throw new VkClientException($"Сохранение медиа не вернуло идентификатор ({prefix}).");
        return string.IsNullOrEmpty(accessKey) ? $"{prefix}{ownerId}_{id}" : $"{prefix}{ownerId}_{id}_{accessKey}";
    }

}
