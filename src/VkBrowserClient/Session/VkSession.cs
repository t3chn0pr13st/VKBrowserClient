using System.Text.Json.Serialization;

namespace VkBrowserClient;

/// <summary>
/// Долгоживущее состояние авторизации, которое сохраняется между запусками.
///
/// Модель повторяет то, как устроен веб-мессенджер:
///  • <see cref="Cookies"/> — долгоживущая сессия (главная — httpOnly remixsid на .vk.ru);
///  • <see cref="WebToken"/> — короткоживущий токен (~18 минут), который мятится из cookies
///    и используется как access_token для web.api.vk.ru.
///
/// ВНИМАНИЕ: файл сессии эквивалентен паролю — он даёт полный доступ к аккаунту.
/// Храните его только в защищённом месте (см. <see cref="FileSessionStore"/>).
/// </summary>
public sealed class VkSession
{
    /// <summary>id пользователя (из ответа web_token).</summary>
    public long UserId { get; set; }

    /// <summary>Cookies браузерной сессии.</summary>
    public List<VkCookie> Cookies { get; set; } = new();

    /// <summary>Текущий web-токен (vk1.a....). Может отсутствовать/протухнуть — тогда обновляется.</summary>
    public string? WebToken { get; set; }

    /// <summary>Unix-время истечения web-токена (в секундах).</summary>
    public long WebTokenExpiresAtUnix { get; set; }

    /// <summary>logout_hash из ответа web_token (информационно).</summary>
    public string? LogoutHash { get; set; }

    /// <summary>User-Agent браузера, в котором прошёл вход. Используется для фоновых запросов.</summary>
    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public DateTimeOffset? WebTokenExpiresAt =>
        WebTokenExpiresAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(WebTokenExpiresAtUnix) : null;

    [JsonIgnore]
    public bool HasCookies => Cookies is { Count: > 0 };
}
