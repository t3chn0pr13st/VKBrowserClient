using System.Text.Json;
using Microsoft.Playwright;

namespace VkBrowserClient;

/// <summary>
/// Интерактивный вход через управляемый браузер Playwright (Chromium).
///
/// Открывает видимое окно на vk.ru, пользователь сам проходит VK ID
/// (телефон/пароль/2FA/капча), после чего мы снимаем cookie-сессию — этого
/// достаточно, чтобы дальше работать в фоне без UI (web-токен мятится из cookies).
/// </summary>
public sealed class PlaywrightAuthenticator : IInteractiveAuthenticator, ISessionCookieTopUp
{
    private readonly VkClientOptions _options;

    public PlaywrightAuthenticator(VkClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<VkSession> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        var browser = await LaunchAsync(playwright).ConfigureAwait(false);
        try
        {
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1180, Height = 820 },
            }).ConfigureAwait(false);

            var page = await context.NewPageAsync().ConfigureAwait(false);

            Status($"Открываю браузер для входа: {_options.WebBaseUrl}");
            Status("Пройдите авторизацию в открывшемся окне (телефон/пароль/2FA/капча).");
            Status("Как только вход завершится, окно закроется автоматически.");

            await page.GotoAsync(_options.WebBaseUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            }).ConfigureAwait(false);

            await WaitForLoginAsync(context, page, cancellationToken).ConfigureAwait(false);

            Status("Вход выполнен, сохраняю сессию…");

            // Заходим в мессенджер, чтобы SDK инициализировал и записал стартовый web-токен.
            try
            {
                await page.GotoAsync(_options.WebBaseUrl + "/im", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000,
                }).ConfigureAwait(false);
                await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Не критично: cookies уже есть, токен обновится сам при первом вызове API.
                // Но отмену (Ctrl+C) не проглатываем — она должна дойти до вызывающего кода.
            }

            // Заходим на vkvideo.ru: у него собственный набор cookie на своём домене, и без них
            // не выпустить web-токен приложения live-SDK. Повторная авторизация не нужна —
            // домены связаны SSO, и cookie ставятся молча при первом заходе.
            try
            {
                await page.GotoAsync(_options.LiveSdkWebBaseUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000,
                }).ConfigureAwait(false);
                await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Не критично для остальных возможностей клиента: без этих cookie перестанет
                // работать только live-SDK, и он скажет об этом явно при первом обращении.
            }

            var session = await BuildSessionAsync(context, page, cancellationToken).ConfigureAwait(false);

            if (!session.HasCookies)
                throw new VkAuthenticationException("Не удалось снять cookies сессии после входа.");

            return session;
        }
        finally
        {
            try { await browser.CloseAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    /// <inheritdoc />
    public async Task<bool> TopUpAsync(VkSession session, string host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (!session.HasCookies)
            throw new VkAuthenticationException("Нечего дотягивать: в сессии нет cookies.");

        // Пользовательских действий здесь не требуется, поэтому сначала пробуем без окна.
        // Если VK не отдаёт cookie фоновому браузеру, повторяем с видимым — вход всё равно не нужен.
        foreach (var headless in new[] { true, false })
        {
            if (await TryTopUpAsync(session, host, headless, cancellationToken).ConfigureAwait(false))
                return true;

            Status(headless
                ? $"{host} не отдал cookie фоновому браузеру — повторяю с видимым окном…"
                : $"{host} не отдал cookie и в видимом окне.");
        }

        return false;
    }

    private async Task<bool> TryTopUpAsync(
        VkSession session,
        string host,
        bool headless,
        CancellationToken cancellationToken)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        var browser = await LaunchAsync(playwright, headless).ConfigureAwait(false);
        try
        {
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = string.IsNullOrWhiteSpace(session.UserAgent) ? null : session.UserAgent,
                ViewportSize = new ViewportSize { Width = 1180, Height = 820 },
            }).ConfigureAwait(false);

            var seeded = session.Cookies.Select(ToPlaywrightCookie).Where(x => x is not null).Cast<Cookie>().ToArray();
            if (seeded.Length == 0)
                throw new VkAuthenticationException("Не удалось перенести cookies сессии в браузер.");
            await context.AddCookiesAsync(seeded).ConfigureAwait(false);

            var page = await context.NewPageAsync().ConfigureAwait(false);
            Status($"Открываю https://{host}/ в браузере, чтобы забрать cookie домена…");
            // NetworkIdle у VK не наступает: страница держит соединения постоянно.
            await page.GotoAsync($"https://{host}/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            }).ConfigureAwait(false);
            // Переход между доменами делает скрипт страницы, поэтому даём ему отработать.
            await Task.Delay(4000, cancellationToken).ConfigureAwait(false);

            var fresh = (await context.CookiesAsync().ConfigureAwait(false))
                .Where(c => BelongsTo(c.Domain, host))
                .ToArray();

            // Наличие любых cookie домена ещё ничего не значит: VK ставит служебные и без входа.
            // Признак состоявшегося перехода — авторизационная cookie.
            if (!fresh.Any(c => c.Name.StartsWith("remixdsid", StringComparison.OrdinalIgnoreCase)
                                || c.Name.Equals("remixsid", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            Merge(session, fresh, host);
            Status($"Добрано cookie домена {host}: {fresh.Length}.");
            return true;
        }
        finally
        {
            try { await browser.CloseAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private static void Merge(VkSession session, IEnumerable<BrowserContextCookiesResult> fresh, string host)
    {
        session.Cookies.RemoveAll(c => BelongsTo(c.Domain, host));
        foreach (var c in fresh)
        {
            session.Cookies.Add(new VkCookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain,
                Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                Expires = c.Expires > 0 ? c.Expires : null,
                HttpOnly = c.HttpOnly,
                Secure = c.Secure,
                SameSite = c.SameSite.ToString(),
            });
        }
    }

    private static Cookie? ToPlaywrightCookie(VkCookie cookie)
    {
        if (string.IsNullOrEmpty(cookie.Name) || string.IsNullOrWhiteSpace(cookie.Domain))
            return null;

        return new Cookie
        {
            Name = cookie.Name,
            Value = cookie.Value ?? "",
            Domain = cookie.Domain,
            Path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
            // Playwright ждёт -1 для сессионных cookie, а не отсутствие значения.
            Expires = cookie.Expires is > 0 ? (float)cookie.Expires.Value : -1,
            HttpOnly = cookie.HttpOnly,
            Secure = cookie.Secure,
        };
    }

    private static bool BelongsTo(string? domain, string host)
    {
        var value = (domain ?? "").TrimStart('.');
        return value.Equals(host, StringComparison.OrdinalIgnoreCase)
               || value.EndsWith('.' + host, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IBrowser> LaunchAsync(IPlaywright playwright, bool headless = false)
    {
        // Для входа окно должно быть видимым (капча/2FA); для дозабора cookie — не обязано.
        var launchOptions = new BrowserTypeLaunchOptions { Headless = headless };
        try
        {
            return await playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
        }
        catch (PlaywrightException ex) when (LooksLikeMissingBrowser(ex))
        {
            Status("Браузер Chromium для Playwright не установлен — скачиваю (однократно)…");
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                var script = Path.Combine(AppContext.BaseDirectory, "playwright.ps1");
                throw new VkAuthenticationException(
                    $"Не удалось установить браузер Playwright (код {exitCode}). " +
                    $"Установите вручную: pwsh \"{script}\" install chromium", ex);
            }

            return await playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
        }
    }

    private async Task WaitForLoginAsync(IBrowserContext context, IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.LoginTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var cookies = await context.CookiesAsync().ConfigureAwait(false);
                if (cookies.Any(c => string.Equals(c.Name, "remixsid", StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrEmpty(c.Value)))
                    return;

                // Резервный признак: window.vk.id > 0 (появляется на страницах vk.ru после входа).
                var id = await page.EvaluateAsync<long>(
                    "() => { try { return (window['vk'] && window['vk']['id']) || 0; } catch (e) { return 0; } }")
                    .ConfigureAwait(false);
                if (id > 0)
                    return;
            }
            catch (PlaywrightException ex) when (IsClosed(ex))
            {
                throw new VkAuthenticationException("Окно браузера закрыто до завершения входа.", ex);
            }
            catch (PlaywrightException)
            {
                // Переходная ошибка во время навигации (контекст исполнения уничтожен) — повторим.
            }

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        throw new VkAuthenticationException(
            $"Истекло время ожидания входа ({_options.LoginTimeout.TotalMinutes:0} мин).");
    }

    private async Task<VkSession> BuildSessionAsync(IBrowserContext context, IPage page, CancellationToken cancellationToken)
    {
        var session = new VkSession();

        // User-Agent браузера — используем его же для фоновых запросов.
        try { session.UserAgent = await page.EvaluateAsync<string>("() => navigator.userAgent").ConfigureAwait(false); }
        catch { /* оставим дефолтный из опций */ }

        // Cookies.
        var cookies = await context.CookiesAsync().ConfigureAwait(false);
        foreach (var c in cookies)
        {
            session.Cookies.Add(new VkCookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain,
                Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                Expires = c.Expires > 0 ? c.Expires : null,
                HttpOnly = c.HttpOnly,
                Secure = c.Secure,
                SameSite = c.SameSite.ToString(),
            });
        }

        // Стартовый web-токен из localStorage (не обязателен — обновится сам).
        try
        {
            var raw = await page.EvaluateAsync<string?>(
                $"() => localStorage.getItem('{_options.WebAppId}:web_token:login:auth')").ConfigureAwait(false);
            if (!string.IsNullOrEmpty(raw))
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("access_token", out var at) && at.GetString() is { Length: > 0 } token)
                {
                    session.WebToken = token;
                    if (root.TryGetProperty("expires", out var exp) && exp.TryGetInt64(out var e))
                        session.WebTokenExpiresAtUnix = e;
                    if (root.TryGetProperty("user_id", out var uid) && uid.TryGetInt64(out var u))
                        session.UserId = u;
                }
            }
        }
        catch { /* игнорируем: токен не обязателен на этом этапе */ }

        // Если id не удалось получить из токена — берём из window.vk.id.
        if (session.UserId == 0)
        {
            try
            {
                session.UserId = await page.EvaluateAsync<long>(
                "() => { try { return (window['vk'] && window['vk']['id']) || 0; } catch (e) { return 0; } }")
                .ConfigureAwait(false);
            }
            catch { /* останется 0, заполнится при первом web_token */ }
        }

        return session;
    }

    private static bool LooksLikeMissingBrowser(PlaywrightException ex)
    {
        var m = ex.Message;
        return m.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
               || m.Contains("playwright install", StringComparison.OrdinalIgnoreCase)
               || m.Contains("download new browsers", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClosed(PlaywrightException ex) =>
        ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase);

    private void Status(string message) => _options.StatusCallback?.Invoke(message);
}
