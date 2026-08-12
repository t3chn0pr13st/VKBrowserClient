namespace VkBrowserClient;

/// <summary>
/// Настройки клиента. Значения по умолчанию соответствуют тому, что использует
/// веб-мессенджер vk.ru (client_id = 6287487, домен web.api.vk.ru, версия API 5.282).
/// Менять их обычно не требуется — они и делают клиент «неотличимым» от браузера.
/// </summary>
public sealed class VkClientOptions
{
    /// <summary>client_id веб-приложения VK (мессенджер). Именно от его имени работает браузер.</summary>
    public string WebAppId { get; set; } = "6287487";

    /// <summary>Версия API, которую шлёт веб-клиент.</summary>
    public string ApiVersion { get; set; } = "5.282";

    /// <summary>Хост API методов (аналог api.vk.com/method, но для веб-клиента).</summary>
    public string ApiBaseUrl { get; set; } = "https://web.api.vk.ru";

    /// <summary>Хост, выдающий короткоживущий web-токен по cookie-сессии.</summary>
    public string LoginBaseUrl { get; set; } = "https://login.vk.ru";

    /// <summary>Основной хост, на котором происходит вход.</summary>
    public string WebBaseUrl { get; set; } = "https://vk.ru";

    /// <summary>
    /// User-Agent для фоновых HTTP-запросов. Если сессия сохранила UA браузера,
    /// используется он (это снижает вероятность инвалидации сессии антифродом).
    /// </summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>Сколько ждать, пока пользователь завершит вход в открывшемся браузере.</summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Запас перед фактическим истечением web-токена, при котором он обновляется заранее.
    /// Токен живёт ~18–20 минут, поэтому запас в минуту безопасен.
    /// </summary>
    public TimeSpan TokenExpirySkew { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Максимальное время одного потокового upload-запроса.</summary>
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(30);

    // --- live-SDK VK Видео (apisdk.live.vkvideo.ru) ---------------------------

    /// <summary>
    /// client_id приложения live-SDK VK Видео. Это НЕ <see cref="WebAppId"/> и не app_id
    /// VK Видео web (52461373): SDK-токен выдаётся только в обмен на web-токен этого приложения.
    /// </summary>
    public string LiveSdkAppId { get; set; } = "53729707";

    /// <summary>Хост live-SDK: каналы, слоты, создание и настройка эфиров.</summary>
    public string LiveSdkBaseUrl { get; set; } = "https://apisdk.live.vkvideo.ru";

    /// <summary>Значение заголовка <c>X-App</c>, которым представляется веб-клиент live-SDK.</summary>
    public string LiveSdkAppHeader { get; set; } = "streams_web";

    /// <summary>Значение заголовка <c>X-SDK-App</c>.</summary>
    public string LiveSdkClientHeader { get; set; } = "vkvideo_live_app";

    /// <summary>
    /// Запас перед истечением SDK-токена, при котором он выпускается заново.
    /// Токен живёт 30 суток, поэтому запас крупный: полсуток ничего не стоят,
    /// а токен, протухший посреди подготовки эфира, стоит дорого.
    /// </summary>
    public TimeSpan LiveSdkTokenExpirySkew { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Фабрика интерактивного аутентификатора. По умолчанию — Playwright.
    /// Позволяет подменить способ входа (например, в тестах).
    /// </summary>
    public Func<VkClientOptions, IInteractiveAuthenticator>? AuthenticatorFactory { get; set; }

    /// <summary>
    /// Колбэк для статусных сообщений (открытие браузера, ожидание входа и т.п.).
    /// В консольном примере обычно задаётся как <c>Console.WriteLine</c>.
    /// </summary>
    public Action<string>? StatusCallback { get; set; }

    // Test seams remain internal so the public client does not expose transport internals.
    internal Func<HttpMessageHandler>? ApiHttpMessageHandlerFactory { get; set; }
    internal Func<HttpMessageHandler>? UploadHttpMessageHandlerFactory { get; set; }
    internal Func<HttpMessageHandler>? LiveSdkHttpMessageHandlerFactory { get; set; }
}
