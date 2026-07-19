using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Публикация клипов (VK Клипы) — по флоу веб-клиента:
/// shortVideo.create → загрузка на CDN → shortVideo.encodeProgress → shortVideo.edit → shortVideo.publish.
///
/// Замечания:
///  • крупные клипы веб-клиент грузит чанками (4 канала); здесь — одиночный POST,
///    чего достаточно для роликов умеренного размера;
///  • обложка выбирается автоматически (кадр по умолчанию) — выбор конкретного кадра не реализован.
/// </summary>
public sealed class VkClipsService
{
    private readonly VkClient _client;

    internal VkClipsService(VkClient client) => _client = client;

    /// <summary>Опубликовать клип из файла.</summary>
    public Task<VkClipResult> PublishFromFileAsync(
        string path, string? description = null, bool alsoPostToWall = true,
        long? groupId = null, CancellationToken cancellationToken = default)
        => PublishAsync(File.ReadAllBytes(path), Path.GetFileName(path), description, alsoPostToWall, groupId, cancellationToken);

    /// <summary>
    /// Опубликовать клип из байтов видео (вертикальное короткое видео).
    /// <paramref name="alsoPostToWall"/> — также разместить запись на стене (как в вебе).
    /// <paramref name="groupId"/> — опубликовать от имени сообщества (иначе в профиль).
    /// </summary>
    public async Task<VkClipResult> PublishAsync(
        byte[] video, string fileName, string? description = null, bool alsoPostToWall = true,
        long? groupId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (video.Length < 16384)
            throw new ArgumentException("Клип слишком мал: VK требует минимум 16 КБ.", nameof(video));

        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);

        // 1) shortVideo.create — резервирует клип и отдаёт upload_url.
        long ownerId, videoId;
        string uploadUrl;
        using (var doc = await api.CallAsync("shortVideo.create", new Dictionary<string, string>
        {
            ["file_size"] = video.Length.ToString(),
            ["group_id"] = groupId?.ToString() ?? "",
        }, cancellationToken).ConfigureAwait(false))
        {
            var r = VkWebApi.GetResponseOrThrow(doc, "shortVideo.create");
            ownerId = r.TryGetProperty("owner_id", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
            videoId = r.TryGetProperty("video_id", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
            uploadUrl = r.TryGetProperty("upload_url", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(uploadUrl) || videoId == 0)
                throw new VkClientException("shortVideo.create не вернул upload_url/video_id.");
        }

        // 2) Загрузка файла на CDN (поле video_file), как у обычного видео.
        string videoHash;
        using (var up = await api.UploadFileAsync(uploadUrl, "video_file", video, fileName, "video/mp4", cancellationToken).ConfigureAwait(false))
        {
            if (up.RootElement.TryGetProperty("error", out _))
                throw new VkClientException($"CDN отклонил клип: {up.RootElement.GetRawText()}");
            videoHash = up.RootElement.TryGetProperty("video_hash", out var vh) ? vh.GetString() ?? "" : "";
        }

        // 3) Дождаться завершения кодирования (best-effort; без этого публикация может не пройти).
        await WaitEncodedAsync(api, videoId, ownerId, videoHash, cancellationToken).ConfigureAwait(false);

        // 4) Метаданные: описание и приватность.
        var editParams = new Dictionary<string, string>
        {
            ["video_id"] = videoId.ToString(),
            ["owner_id"] = ownerId.ToString(),
            ["privacy_view"] = "all",
            ["privacy_comment"] = "all",
        };
        if (!string.IsNullOrEmpty(description))
            editParams["description"] = description;
        using (await api.CallAsync("shortVideo.edit", editParams, cancellationToken).ConfigureAwait(false)) { }

        // 5) Публикация.
        using (var pub = await api.CallAsync("shortVideo.publish", new Dictionary<string, string>
        {
            ["video_id"] = videoId.ToString(),
            ["owner_id"] = ownerId.ToString(),
            ["wallpost"] = alsoPostToWall ? "1" : "0",
            ["publish_date"] = "0",
            ["license_agree"] = "1",
            ["ref"] = "clips_viewer",
        }, cancellationToken).ConfigureAwait(false))
        {
            VkWebApi.GetResponseOrThrow(pub, "shortVideo.publish");
        }

        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return new VkClipResult { OwnerId = ownerId, VideoId = videoId };
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
}
