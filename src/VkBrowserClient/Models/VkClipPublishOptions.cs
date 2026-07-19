namespace VkBrowserClient;

/// <summary>Кому видно/кто комментирует клип.</summary>
public enum VkClipPrivacy
{
    /// <summary>Все.</summary>
    All,
    /// <summary>Друзья.</summary>
    Friends,
    /// <summary>Только я.</summary>
    OnlyMe,
}

/// <summary>
/// Параметры публикации клипа — соответствуют галочкам в окне публикации VK Клипов.
/// </summary>
public sealed class VkClipPublishOptions
{
    /// <summary>Описание клипа.</summary>
    public string? Description { get; init; }

    /// <summary>Кто может смотреть (privacy_view).</summary>
    public VkClipPrivacy View { get; init; } = VkClipPrivacy.All;

    /// <summary>Кто может комментировать (privacy_comment).</summary>
    public VkClipPrivacy Comment { get; init; } = VkClipPrivacy.All;

    /// <summary>Разрешить дуэты (can_make_duet).</summary>
    public bool AllowDuets { get; init; } = true;

    /// <summary>Также разместить запись на стене (wallpost).</summary>
    public bool PostToWall { get; init; } = true;

    /// <summary>Опубликовать от имени сообщества (иначе — в профиль).</summary>
    public long? GroupId { get; init; }

    /// <summary>Отложенная публикация (publish_date). По умолчанию — сразу.</summary>
    public DateTimeOffset? PublishAt { get; init; }

    internal static string Privacy(VkClipPrivacy p) => p switch
    {
        VkClipPrivacy.Friends => "friends",
        VkClipPrivacy.OnlyMe => "only_me",
        _ => "all",
    };
}
