namespace VkBrowserClient;

/// <summary>
/// Кто может смотреть <b>трансляцию</b> (не запись). Это отдельная настройка от
/// <see cref="VkLivePrivacy"/>, которая управляет приватностью записи через <c>video.*</c>.
/// Закрывать нужно обе: закрытая запись при публичном эфире зрителя не остановит.
/// </summary>
public enum VkLiveSdkPermission
{
    /// <summary>Все пользователи.</summary>
    Public,

    /// <summary>Подписчики сообщества.</summary>
    Followers,

    /// <summary>Редакторы и администраторы.</summary>
    Admins,

    /// <summary>У кого есть ссылка.</summary>
    ByLink,
}

/// <summary>Настройки существующего эфира, прочитанные с сервера.</summary>
public sealed class VkLiveSdkSettings
{
    /// <summary>Кто может смотреть трансляцию — фактическое значение на слоте.</summary>
    public required VkLiveSdkPermission Permission { get; init; }

    /// <summary>Заголовок трансляции, как его разобрал VK.</summary>
    public required string Title { get; init; }
}

/// <summary>Параметры создания эфира сообщества через live-SDK VK Видео.</summary>
public sealed class VkLiveSdkCreateOptions
{
    /// <summary>Идентификатор сообщества (положительный, без минуса).</summary>
    public required long GroupId { get; init; }

    /// <summary>Заголовок трансляции.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Кто может смотреть эфир. Уходит прямо в создающий запрос, поэтому приватность
    /// верна с первой секунды — окна, в котором эфир публичен, не возникает.
    /// </summary>
    public VkLiveSdkPermission Permission { get; init; } = VkLiveSdkPermission.Public;

    /// <summary>Отображаемое имя автора. В вебе сюда уходит название сообщества.</summary>
    public string? ChannelName { get; init; }

    /// <summary>Сохранять запись трансляции.</summary>
    public bool RecordStream { get; init; } = true;

    /// <summary>Отключить чат.</summary>
    public bool DisableChat { get; init; }

    /// <summary>Уведомить подписчиков о начале.</summary>
    public bool NotifyFollowers { get; init; }

    /// <summary>Опубликовать пост в сообществе ВКонтакте.</summary>
    public bool CreateWallPost { get; init; }

    /// <summary>Запретить перемотку трансляции.</summary>
    public bool DisablePlayback { get; init; }

    /// <summary>Режим предварительного просмотра.</summary>
    public bool PreviewMode { get; init; }

    /// <summary>Ссылка из ВКонтакте, показываемая поверх трансляции.</summary>
    public string? AdditionalUrl { get; init; }

    /// <summary>Описание трансляции.</summary>
    public string? Description { get; init; }

    internal void Validate()
    {
        if (GroupId <= 0)
            throw new ArgumentException("GroupId должен быть положительным идентификатором сообщества.", nameof(GroupId));
        if (string.IsNullOrWhiteSpace(Title))
            throw new ArgumentException("Title обязателен.", nameof(Title));
    }
}

/// <summary>
/// Эфир, созданный через live-SDK. Один ответ отдаёт сразу всё, что нужно дальше:
/// слот для настройки приватности, идентификатор VK-видео для ссылок и ключ входного потока.
/// </summary>
public sealed class VkLiveSdkStream
{
    /// <summary>Канал, которому принадлежит слот (например, <c>channel35338325</c>).</summary>
    public required string ChannelUrl { get; init; }

    /// <summary>Слот эфира (например, <c>sl_163026</c>) — адресует его во всех manage-запросах.</summary>
    public required string SlotUrl { get; init; }

    /// <summary>Числовой идентификатор слота.</summary>
    public long SlotId { get; init; }

    /// <summary>Владелец VK-видео (для сообщества — отрицательный).</summary>
    public long VkOwnerId { get; init; }

    /// <summary>Идентификатор VK-видео, под которым эфир виден в сообществе.</summary>
    public long VkVideoId { get; init; }

    /// <summary>Приватность эфира, как её подтвердил сервер. Сверяйте с запрошенной.</summary>
    public VkLiveSdkPermission Permission { get; init; }

    /// <summary>Одноразовый ли ключ (в вебе — переключатель «Одноразовый/Постоянный ключ»).</summary>
    public bool IsTemporary { get; init; }

    /// <summary>Параметры входного потока. URL и ключ — секреты.</summary>
    public required VkLiveIngest Ingest { get; init; }

    /// <summary>Безопасная ссылка-вложение без ключей.</summary>
    public string Reference => $"video{VkOwnerId}_{VkVideoId}";

    /// <summary>Страница эфира.</summary>
    public string Url => $"https://vkvideo.ru/live{VkOwnerId}_{VkVideoId}";

    /// <summary>Безопасное строковое представление без ingest-ключей.</summary>
    public override string ToString() => $"{Reference} ({ChannelUrl}/{SlotUrl})";
}
