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

    /// <summary>
    /// Bearer-токен live-SDK VK Видео (apisdk.live.vkvideo.ru). В отличие от <see cref="WebToken"/>
    /// живёт ~30 суток и выпускается в обмен на web-токен приложения live-SDK.
    /// </summary>
    public string? LiveSdkToken { get; set; }

    /// <summary>
    /// refresh_token к <see cref="LiveSdkToken"/>. Сохраняется, но пока не используется:
    /// эндпоинт обновления не наблюдался, а выпуск токена заново стоит два запроса.
    /// </summary>
    public string? LiveSdkRefreshToken { get; set; }

    /// <summary>Unix-время истечения SDK-токена (в секундах).</summary>
    public long LiveSdkTokenExpiresAtUnix { get; set; }

    /// <summary>
    /// device_id, под которым выпущен SDK-токен; он же уходит в заголовок <c>X-From-Id</c>.
    /// Генерируется один раз и переживает перевыпуск токена — так клиент остаётся для VK
    /// одним и тем же устройством, а не новым при каждом запуске.
    /// </summary>
    public string? LiveSdkDeviceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public DateTimeOffset? WebTokenExpiresAt =>
        WebTokenExpiresAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(WebTokenExpiresAtUnix) : null;

    [JsonIgnore]
    public DateTimeOffset? LiveSdkTokenExpiresAt =>
        LiveSdkTokenExpiresAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(LiveSdkTokenExpiresAtUnix) : null;

    [JsonIgnore]
    public bool HasCookies => Cookies is { Count: > 0 };
}
