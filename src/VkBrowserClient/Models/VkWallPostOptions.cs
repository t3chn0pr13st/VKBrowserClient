namespace VkBrowserClient;

/// <summary>Параметры публикации записи на личной стене или в сообществе.</summary>
public sealed class VkWallPostOptions
{
    /// <summary>
    /// Положительный id сообщества. Если не задан, публикация идёт на стену текущего пользователя.
    /// </summary>
    public long? CommunityId { get; set; }

    /// <summary>Публиковать от имени сообщества. Применяется только при <see cref="CommunityId"/>.</summary>
    public bool FromCommunity { get; set; } = true;

    /// <summary>Ограничить запись друзьями; доступно только для личной стены.</summary>
    public bool FriendsOnly { get; set; }

    /// <summary>Время отложенной публикации. null означает публикацию сейчас.</summary>
    public DateTimeOffset? PublishAt { get; set; }

    internal long? ValidateAndGetCommunityId()
    {
        if (CommunityId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(CommunityId), "CommunityId должен быть положительным.");
        if (CommunityId.HasValue && FriendsOnly)
            throw new ArgumentException("FriendsOnly нельзя использовать для стены сообщества.", nameof(FriendsOnly));
        if (PublishAt is { } publishAt && publishAt <= DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(PublishAt), "Время отложенной публикации должно быть в будущем.");
        return CommunityId;
    }
}
