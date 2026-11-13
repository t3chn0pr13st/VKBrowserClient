namespace VkBrowserClient;

internal static class VkCookieAuthentication
{
    internal static bool BelongsTo(string domain, string host) =>
        host.Equals(domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase);

    internal static bool IsUsable(VkCookie cookie, string host, TimeSpan skew) =>
        BelongsTo(cookie.Domain, host)
        && (cookie.Name.Equals("remixsid", StringComparison.OrdinalIgnoreCase)
            || cookie.Name.Equals("remixdsid", StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrWhiteSpace(cookie.Value)
        && (cookie.Expires is null or < 0
            || cookie.Expires > DateTimeOffset.UtcNow.Add(skew).ToUnixTimeSeconds());
}
