using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Эфиры сообществ через live-SDK VK Видео — тот путь, которым идёт сама страница трансляций.
///
/// Отличие от <see cref="VkLiveService"/> (официальные <c>video.*</c>) принципиальное:
/// здесь приватность <b>эфира</b> задаётся прямо при создании, и ответ сразу отдаёт слот,
/// идентификатор VK-видео и ключ входного потока. У <c>video.startStreaming</c> настройки
/// приватности эфира нет вовсе — она есть только у записи.
/// </summary>
public sealed class VkLiveSdkService
{
    private readonly VkClient _client;

    internal VkLiveSdkService(VkClient client) => _client = client;

    /// <summary>
    /// Создать эфир сообщества с заданной приватностью.
    /// Сохраните <see cref="VkLiveSdkStream.ChannelUrl"/> и <see cref="VkLiveSdkStream.SlotUrl"/>:
    /// по ним потом адресуются чтение и изменение настроек эфира.
    /// </summary>
    public async Task<VkLiveSdkStream> CreateGroupStreamAsync(
        VkLiveSdkCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var form = new Dictionary<string, string>
        {
            ["vk_group_id"] = options.GroupId.ToString(),
            ["vk_permissions"] = Permission(options.Permission),
            ["title_data"] = TitleData(options.Title),
            ["is_should_record"] = Bool(options.RecordStream),
            ["is_chat_disabled"] = Bool(options.DisableChat),
            ["is_vk_notify_followers"] = Bool(options.NotifyFollowers),
            ["is_vk_wallpost_create"] = Bool(options.CreateWallPost),
            ["is_playback_disabled"] = Bool(options.DisablePlayback),
            ["use_stream_preview_mode"] = Bool(options.PreviewMode),
            ["vk_additional_url"] = options.AdditionalUrl ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(options.ChannelName))
            form["name"] = options.ChannelName;
        if (!string.IsNullOrWhiteSpace(options.Description))
            form["vk_description"] = options.Description;

        var api = await _client.RequireLiveSdkApiAsync(cancellationToken).ConfigureAwait(false);
        using var data = await api
            .SendAsync(HttpMethod.Post, "/v1/channel/manage/vk/stream/", form, cancellationToken)
            .ConfigureAwait(false);

        var stream = Map(data.RootElement);
        await _client.PersistSessionAsync(cancellationToken).ConfigureAwait(false);
        return stream;
    }

    /// <summary>
    /// Прочитать фактическую приватность эфира. Нужна для fail-closed проверки после подготовки:
    /// доверять запрошенному значению нельзя, сверяйте с тем, что реально стоит на слоте.
    /// </summary>
    public async Task<VkLiveSdkPermission> GetStreamPermissionAsync(
        string channelUrl,
        string slotUrl,
        CancellationToken cancellationToken = default)
        => (await GetStreamSettingsAsync(channelUrl, slotUrl, cancellationToken).ConfigureAwait(false)).Permission;

    /// <summary>Настройки эфира, как их отдаёт сервер.</summary>
    public async Task<VkLiveSdkSettings> GetStreamSettingsAsync(
        string channelUrl,
        string slotUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotUrl);

        // Именно manage-путь: публичный /stream/slot/ отдаёт состояние эфира для зрителя,
        // без настроек приватности. Это тот же путь, которым идёт изменение настроек.
        var api = await _client.RequireLiveSdkApiAsync(cancellationToken).ConfigureAwait(false);
        using var data = await api
            .SendAsync(HttpMethod.Get, $"/v1/channel/{channelUrl}/manage/vk/stream/{slotUrl}", null, cancellationToken)
            .ConfigureAwait(false);

        var slot = FindSlot(data.RootElement);
        var raw = slot is null ? null : String(slot.Value, "vkPermission");
        if (string.IsNullOrWhiteSpace(raw))
            throw new VkClientException(
                $"Ответ по слоту {channelUrl}/{slotUrl} не содержит vkPermission. " +
                VkSafeErrorDetails.DescribeShape(data.RootElement));

        return new VkLiveSdkSettings
        {
            Permission = ParsePermission(raw),
            Title = String(slot!.Value, "title")?.Trim() ?? string.Empty,
        };
    }

    /// <summary>
    /// Находит объект слота в ответе. Это API кладёт его то в корень, то в <c>streamSlot</c>,
    /// то в <c>stream</c>, поэтому опознаём по наличию <c>vkPermission</c>.
    /// </summary>
    private static JsonElement? FindSlot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (String(root, "vkPermission") is { Length: > 0 })
            return root;

        foreach (var wrapper in new[] { "streamSlot", "stream", "slot" })
        {
            if (root.TryGetProperty(wrapper, out var nested)
                && nested.ValueKind == JsonValueKind.Object
                && String(nested, "vkPermission") is { Length: > 0 })
            {
                return nested;
            }
        }

        return null;
    }

    // --- маппинг -------------------------------------------------------------

    private static VkLiveSdkStream Map(JsonElement data)
    {
        if (!data.TryGetProperty("streamSlot", out var slot) || slot.ValueKind != JsonValueKind.Object)
            throw new VkClientException(
                "Ответ создания эфира не содержит streamSlot: " + VkSafeErrorDetails.Describe(data));

        var slotUrl = String(slot, "slotUrl");
        if (string.IsNullOrWhiteSpace(slotUrl))
            throw new VkClientException("Ответ создания эфира не содержит slotUrl — адресовать эфир нечем.");

        var channelUrl = data.TryGetProperty("channel", out var channel) ? String(channel, "channelUrl") : null;
        if (string.IsNullOrWhiteSpace(channelUrl))
            throw new VkClientException("Ответ создания эфира не содержит channelUrl.");

        if (!data.TryGetProperty("credentials", out var credentials) ||
            credentials.ValueKind != JsonValueKind.Object)
        {
            throw new VkClientException("Ответ создания эфира не содержит credentials входного потока.");
        }

        var server = String(credentials, "streamServer");
        var key = String(credentials, "streamKey");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(key))
            throw new VkClientException("Ответ создания эфира не содержит URL и ключ входного потока.");

        var video = data.TryGetProperty("video", out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : default;

        return new VkLiveSdkStream
        {
            ChannelUrl = channelUrl,
            SlotUrl = slotUrl,
            SlotId = Int64(slot, "id"),
            VkOwnerId = video.ValueKind == JsonValueKind.Object ? Int64(video, "vkOwnerId") : 0,
            VkVideoId = video.ValueKind == JsonValueKind.Object ? Int64(video, "vkVideoId") : 0,
            Permission = ParsePermission(String(slot, "vkPermission")),
            IsTemporary = slot.TryGetProperty("isTemporary", out var t) && t.ValueKind == JsonValueKind.True,
            Ingest = new VkLiveIngest { Url = server, Key = key },
        };
    }

    /// <summary>
    /// Заголовок уходит не строкой, а вложенным блочным документом редактора VK.
    ///
    /// Форма снята с живого создания; список инлайновых стилей отправляется пустым —
    /// в снятом запросе там был непрозрачный диапазон, назначение которого выяснить не удалось,
    /// а пустой список означает «без оформления».
    /// </summary>
    private static string TitleData(string title)
    {
        var content = JsonSerializer.Serialize(new object?[] { title, "unstyled", Array.Empty<object>() });
        return JsonSerializer.Serialize(new[]
        {
            new TitleBlock("text", content, string.Empty),
            new TitleBlock("text", string.Empty, "BLOCK_END"),
        });
    }

    private sealed record TitleBlock(string type, string content, string modificator);

    private static string Permission(VkLiveSdkPermission permission) => permission switch
    {
        VkLiveSdkPermission.Public => "public",
        VkLiveSdkPermission.Followers => "followers",
        VkLiveSdkPermission.Admins => "admins",
        VkLiveSdkPermission.ByLink => "by_link",
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, "Неизвестное значение приватности эфира."),
    };

    private static VkLiveSdkPermission ParsePermission(string? raw) => raw switch
    {
        "public" => VkLiveSdkPermission.Public,
        "followers" => VkLiveSdkPermission.Followers,
        "admins" => VkLiveSdkPermission.Admins,
        "by_link" => VkLiveSdkPermission.ByLink,
        _ => throw new VkClientException($"VK вернул неизвестную приватность эфира: '{raw}'."),
    };

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Int64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
}
