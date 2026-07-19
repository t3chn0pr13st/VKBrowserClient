using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Трёхшаговая загрузка фото как в вебе: get*UploadServer → POST файла → save*Photo.
/// Возвращает ссылку-вложение вида photo{owner}_{id}[_{access_key}].
/// </summary>
internal sealed class VkPhotoUploader(VkWebApi api)
{
    public Task<string> UploadForMessageAsync(long peerId, VkImage image, CancellationToken ct) =>
        UploadAsync("photos.getMessagesUploadServer",
            new Dictionary<string, string> { ["peer_id"] = peerId.ToString() },
            "photos.saveMessagesPhoto", image, ct);

    public Task<string> UploadForWallAsync(VkImage image, CancellationToken ct) =>
        UploadAsync("photos.getWallUploadServer",
            new Dictionary<string, string>(),
            "photos.saveWallPhoto", image, ct);

    private async Task<string> UploadAsync(
        string getServerMethod, Dictionary<string, string> serverParams,
        string saveMethod, VkImage image, CancellationToken ct)
    {
        string uploadUrl;
        using (var srv = await api.CallAsync(getServerMethod, serverParams, ct).ConfigureAwait(false))
        {
            var resp = VkWebApi.GetResponseOrThrow(srv, getServerMethod);
            uploadUrl = resp.TryGetProperty("upload_url", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(uploadUrl))
                throw new VkClientException($"{getServerMethod} не вернул upload_url.");
        }

        var uploaded = await api.UploadPhotoAsync(uploadUrl, image, ct).ConfigureAwait(false);

        using var saved = await api.CallAsync(saveMethod, new Dictionary<string, string>
        {
            ["photo"] = uploaded.Photo,
            ["server"] = uploaded.Server.ToString(),
            ["hash"] = uploaded.Hash,
        }, ct).ConfigureAwait(false);

        return BuildReference(VkWebApi.GetResponseOrThrow(saved, saveMethod));
    }

    private static string BuildReference(JsonElement savedArray)
    {
        if (savedArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in savedArray.EnumerateArray())
            {
                var ownerId = el.TryGetProperty("owner_id", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
                var id = el.TryGetProperty("id", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
                var accessKey = el.TryGetProperty("access_key", out var ak) ? ak.GetString() : null;
                if (id == 0)
                    continue;
                return string.IsNullOrEmpty(accessKey) ? $"photo{ownerId}_{id}" : $"photo{ownerId}_{id}_{accessKey}";
            }
        }
        throw new VkClientException("Сохранение фото не вернуло идентификатор.");
    }
}
