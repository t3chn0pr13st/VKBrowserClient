namespace VkBrowserClient;

/// <summary>
/// Интерактивный вход: открывает браузер, ждёт, пока пользователь пройдёт
/// авторизацию VK ID (телефон/пароль/2FA/капча), и снимает cookie-сессию.
/// </summary>
public interface IInteractiveAuthenticator
{
    Task<VkSession> AuthenticateAsync(CancellationToken cancellationToken = default);
}
