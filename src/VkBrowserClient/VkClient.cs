namespace VkBrowserClient;

/// <summary>
/// Высокоуровневый клиент vk.ru: вход через браузер, сохранение/обновление сессии,
/// чтение и отправка сообщений (в т.ч. с фото) и публикация записей — тем же способом,
/// что и веб-клиент.
///
/// Типичный сценарий:
/// <code>
/// await using var client = VkClient.Create("session.json", o =&gt; o.StatusCallback = Console.WriteLine);
/// await client.EnsureAuthenticatedAsync();               // 1-й запуск откроет браузер, дальше — тихо
/// var page = await client.Messages.GetConversationsAsync(10);
/// foreach (var c in page.Items) Console.WriteLine(c.Title);
/// </code>
/// </summary>
public sealed class VkClient : IAsyncDisposable
{
    private readonly VkClientOptions _options;
    private readonly ISessionStore _store;
    private readonly IInteractiveAuthenticator _authenticator;

    private VkSession? _session;
    private VkWebApi? _api;
    private VkMessagesService? _messages;
    private VkWallService? _wall;
    private VkClipsService? _clips;
    private VkLiveService? _live;

    public VkClient(ISessionStore store, VkClientOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new VkClientOptions();
        _authenticator = _options.AuthenticatorFactory?.Invoke(_options)
                         ?? new PlaywrightAuthenticator(_options);
    }

    /// <summary>Создать клиент с файловым хранилищем сессии по указанному пути.</summary>
    public static VkClient Create(string sessionFilePath, Action<VkClientOptions>? configure = null)
    {
        var options = new VkClientOptions();
        configure?.Invoke(options);
        return new VkClient(new FileSessionStore(sessionFilePath, options.StatusCallback), options);
    }

    /// <summary>Личные сообщения и беседы.</summary>
    public VkMessagesService Messages => _messages ??= new VkMessagesService(this);

    /// <summary>Записи на стене.</summary>
    public VkWallService Wall => _wall ??= new VkWallService(this);

    /// <summary>Публикация клипов.</summary>
    public VkClipsService Clips => _clips ??= new VkClipsService(this);

    /// <summary>Создание и управление прямыми трансляциями VK Видео.</summary>
    public VkLiveService Live => _live ??= new VkLiveService(this);

    /// <summary>id текущего пользователя (доступен после успешной авторизации).</summary>
    public long? UserId => _session is { UserId: > 0 } s ? s.UserId : null;

    /// <summary>
    /// Гарантирует рабочую сессию: загружает сохранённую, при необходимости открывает
    /// браузер для входа, обновляет web-токен и сохраняет результат.
    /// </summary>
    public async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        _session ??= await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (_session is null || !_session.HasCookies)
        {
            _session = await _authenticator.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            await _store.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
        }

        RebuildApi();

        try
        {
            await _api!.EnsureWebTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VkSessionExpiredException)
        {
            // Cookies больше не годятся — просим войти заново.
            _options.StatusCallback?.Invoke("Сохранённая сессия недействительна — нужен повторный вход.");
            _session = await _authenticator.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            await _store.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
            RebuildApi();
            await _api!.EnsureWebTokenAsync(cancellationToken).ConfigureAwait(false);
        }

        await _store.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Удобный ярлык для <see cref="VkMessagesService.GetConversationsAsync"/>.</summary>
    public Task<ConversationsPage> GetRecentConversationsAsync(int count = 10, CancellationToken cancellationToken = default)
        => Messages.GetConversationsAsync(count, cancellationToken);

    // --- Экспорт/импорт сессии (для переноса на сервер без браузера) ----------

    /// <summary>Выгрузить текущую сессию в отдельный файл (для переноса на сервер).</summary>
    public async Task ExportSessionAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionForExportAsync(cancellationToken).ConfigureAwait(false);
        await new FileSessionStore(destinationPath, _options.StatusCallback).SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Выгрузить текущую сессию как base64-строку (для env-переменной/секрет-менеджера).</summary>
    public async Task<string> ExportSessionToBase64Async(CancellationToken cancellationToken = default)
    {
        var session = await GetSessionForExportAsync(cancellationToken).ConfigureAwait(false);
        return VkSessionSerializer.ToBase64(session);
    }

    /// <summary>Загрузить сессию из файла и сделать её текущей (сохранив в хранилище клиента).</summary>
    public async Task ImportSessionAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var session = await new FileSessionStore(sourcePath).LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null || !session.HasCookies)
            throw new VkClientException($"Файл '{sourcePath}' не содержит корректной сессии.");
        await AdoptSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Загрузить сессию из base64-строки и сделать её текущей.</summary>
    public async Task ImportSessionFromBase64Async(string base64, CancellationToken cancellationToken = default)
    {
        var session = VkSessionSerializer.FromBase64(base64);
        if (!session.HasCookies)
            throw new VkClientException("Импортируемая сессия не содержит cookies.");
        await AdoptSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }

    // --- Внутреннее для под-сервисов -----------------------------------------

    /// <summary>Гарантирует авторизацию и возвращает текущий низкоуровневый API-клиент.</summary>
    internal async Task<VkWebApi> RequireApiAsync(CancellationToken cancellationToken)
    {
        if (_api is null)
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        return _api!;
    }

    /// <summary>Сохранить текущую сессию (web-токен мог обновиться внутри вызова API).</summary>
    internal Task PersistSessionAsync(CancellationToken cancellationToken)
        => _session is null ? Task.CompletedTask : _store.SaveAsync(_session, cancellationToken);

    private async Task<VkSession> GetSessionForExportAsync(CancellationToken cancellationToken)
    {
        _session ??= await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (_session is null || !_session.HasCookies)
            throw new VkClientException("Нет сохранённой сессии для экспорта. Сначала выполните вход.");
        return _session;
    }

    private async Task AdoptSessionAsync(VkSession session, CancellationToken cancellationToken)
    {
        _session = session;
        _api?.Dispose();
        _api = null; // будет пересобран при следующем обращении с новой сессией
        await _store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private void RebuildApi()
    {
        _api?.Dispose();
        _api = new VkWebApi(_session!, _options);
    }

    public ValueTask DisposeAsync()
    {
        _api?.Dispose();
        return ValueTask.CompletedTask;
    }
}
