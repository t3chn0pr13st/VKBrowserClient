namespace VkBrowserClient.Tests;

/// <summary>
/// Test guard: automated tests fail instead of opening Playwright.
/// </summary>
internal sealed class NeverInteractiveAuthenticator : IInteractiveAuthenticator
{
    public Task<VkSession> AuthenticateAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<VkSession>(new VkAuthenticationException(
            "Interactive browser authentication is disabled in automated tests."));
}
