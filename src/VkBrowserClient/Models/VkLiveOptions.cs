namespace VkBrowserClient;

/// <summary>Стандартное значение privacy_view/privacy_comment для трансляции VK.</summary>
public enum VkLivePrivacy
{
    /// <summary>Доступно всем.</summary>
    All,

    /// <summary>Доступно друзьям.</summary>
    Friends,

    /// <summary>Доступно только владельцу.</summary>
    OnlyMe,

    /// <summary>Доступно всем, у кого есть ссылка.</summary>
    ByLink,
}

/// <summary>Параметры официального метода <c>video.startStreaming</c>.</summary>
public sealed class VkLiveStartOptions
{
    /// <summary>
    /// Уже известный video_id. Не задавайте при первом создании. Повтор с сохранённым
    /// идентификатором адресует тот же объект VK и не должен резервировать новый id.
    /// </summary>
    public long? VideoId { get; init; }

    /// <summary>Название трансляции.</summary>
    public string? Name { get; init; }

    /// <summary>Описание трансляции.</summary>
    public string? Description { get; init; }

    /// <summary>Также создать публикацию на стене.</summary>
    public bool PostToWall { get; init; }

    /// <summary>Положительный id сообщества; null означает личный профиль.</summary>
    public long? GroupId { get; init; }

    /// <summary>Кто может смотреть трансляцию.</summary>
    public VkLivePrivacy ViewPrivacy { get; init; } = VkLivePrivacy.All;

    /// <summary>Кто может комментировать трансляцию.</summary>
    public VkLivePrivacy CommentPrivacy { get; init; } = VkLivePrivacy.All;

    /// <summary>Полностью отключить комментарии.</summary>
    public bool DisableComments { get; init; }

    /// <summary>Идентификатор категории из <see cref="VkLiveService.GetCategoriesAsync"/>.</summary>
    public int? CategoryId { get; init; }

    /// <summary>
    /// Опубликовать объект трансляции. Для предварительной подготовки без публичного
    /// анонса оставьте false и переключите состояние отдельной операцией приложения.
    /// </summary>
    public bool Publish { get; init; }

    internal void Validate()
    {
        if (VideoId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(VideoId), "VideoId должен быть положительным.");
        if (GroupId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(GroupId), "GroupId должен быть положительным.");
        if (CategoryId is < 0)
            throw new ArgumentOutOfRangeException(nameof(CategoryId), "CategoryId не может быть отрицательным.");
    }

    internal static string Privacy(VkLivePrivacy privacy) => privacy switch
    {
        VkLivePrivacy.Friends => "friends",
        VkLivePrivacy.OnlyMe => "only_me",
        VkLivePrivacy.ByLink => "by_link",
        _ => "all",
    };
}

/// <summary>Изменяемые поля уже созданной трансляции (<c>video.edit</c>).</summary>
public sealed class VkLiveUpdateOptions
{
    /// <summary>Новое название; null не изменяет поле, пустая строка очищает его.</summary>
    public string? Name { get; init; }

    /// <summary>Новое описание; null не изменяет поле, пустая строка очищает его.</summary>
    public string? Description { get; init; }

    /// <summary>Новая видимость; null не изменяет её.</summary>
    public VkLivePrivacy? ViewPrivacy { get; init; }

    /// <summary>Новая приватность комментариев; null не изменяет её.</summary>
    public VkLivePrivacy? CommentPrivacy { get; init; }

    /// <summary>Включить/отключить комментарии; null не изменяет поле.</summary>
    public bool? DisableComments { get; init; }

    /// <summary>Повторять готовую запись; null не изменяет поле.</summary>
    public bool? Repeat { get; init; }

    internal void Validate()
    {
        if (Name is null && Description is null && ViewPrivacy is null && CommentPrivacy is null &&
            DisableComments is null && Repeat is null)
        {
            throw new ArgumentException("Не задано ни одного поля трансляции для изменения.");
        }
    }
}

/// <summary>Итог смены приватности видеозаписи через приложение «VK Видео».</summary>
public sealed class VkVideoPrivacyResult
{
    /// <summary>VK принял запрос на изменение.</summary>
    public bool Accepted { get; init; }

    /// <summary>
    /// Приватность, прочитанная после сохранения: <c>by_link</c>, <c>all</c> и подобное.
    /// <see langword="null"/> означает «VK не сообщил», а не «доступно всем».
    /// </summary>
    public string? Privacy { get; init; }

    /// <summary>Подтверждено ли ожидаемое значение перечитыванием.</summary>
    public bool Confirms(VkLivePrivacy expected) =>
        Privacy is not null &&
        string.Equals(Privacy, VkLiveStartOptions.Privacy(expected), StringComparison.OrdinalIgnoreCase);
}
