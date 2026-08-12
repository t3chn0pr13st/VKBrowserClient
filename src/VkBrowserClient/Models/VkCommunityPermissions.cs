namespace VkBrowserClient;

/// <summary>
/// Что текущий аккаунт может делать в сообществе. Читается без побочных эффектов —
/// нужен там, где иначе пришлось бы «проверять правá» реальной публикацией.
/// </summary>
public sealed class VkCommunityPermissions
{
    /// <summary>Идентификатор сообщества (положительный, без минуса).</summary>
    public required long GroupId { get; init; }

    /// <summary>Название сообщества, как его вернул VK.</summary>
    public string? Name { get; init; }

    /// <summary>Можно ли публиковать записи на стене сообщества.</summary>
    public bool CanPost { get; init; }

    /// <summary>Является ли текущий аккаунт руководителем сообщества.</summary>
    public bool IsAdmin { get; init; }

    /// <summary>
    /// Уровень прав: 1 — модератор, 2 — редактор, 3 — администратор.
    /// Ноль означает, что VK уровень не вернул, а не «прав нет».
    /// </summary>
    public int AdminLevel { get; init; }

    public override string ToString() =>
        $"VkCommunityPermissions {{ GroupId = {GroupId}, CanPost = {CanPost}, IsAdmin = {IsAdmin} }}";
}
