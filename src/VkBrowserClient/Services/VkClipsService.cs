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
        => PublishAsync(File.ReadAllBytes(path), Path.GetFileName(path), options, cancellationToken);

    /// <summary>Опубликовать клип из байтов видео (вертикальное короткое видео).</summary>
    public async Task<VkClipResult> PublishAsync(
        byte[] video, string fileName, VkClipPublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (video.Length < 16384)
            throw new ArgumentException("Клип слишком мал: VK требует минимум 16 КБ.", nameof(video));

        options ??= new VkClipPublishOptions();
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);

        // 1) shortVideo.create — резервирует клип и отдаёт upload_url.
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

        // 4) Метаданные: описание, приватность, дуэты.
        var editParams = new Dictionary<string, string>
        {
            ["video_id"] = videoId.ToString(),
            ["owner_id"] = ownerId.ToString(),
            ["privacy_view"] = VkClipPublishOptions.Privacy(options.View),
            ["privacy_comment"] = VkClipPublishOptions.Privacy(options.Comment),
            ["can_make_duet"] = options.AllowDuets ? "1" : "0",
        };
        if (!string.IsNullOrEmpty(options.Description))
            editParams["description"] = options.Description;
        using (await api.CallAsync("shortVideo.edit", editParams, cancellationToken).ConfigureAwait(false)) { }

        // 5) Публикация (в т.ч. на стену; при необходимости — отложенная).
        using (var pub = await api.CallAsync("shortVideo.publish", new Dictionary<string, string>
        {
            ["video_id"] = videoId.ToString(),
            ["owner_id"] = ownerId.ToString(),
            ["wallpost"] = options.PostToWall ? "1" : "0",
            ["publish_date"] = (options.PublishAt?.ToUnixTimeSeconds() ?? 0).ToString(),
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
