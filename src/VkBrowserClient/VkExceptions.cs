namespace VkBrowserClient;

/// <summary>Базовое исключение клиента.</summary>
public class VkClientException : Exception
{
    public VkClientException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Ошибка, вернувшаяся от метода API (например, доступ запрещён, флуд-контроль и т.п.).
/// </summary>
public sealed class VkApiException : VkClientException
{
    public int ErrorCode { get; }
    public string Method { get; }

    public VkApiException(string method, int errorCode, string message)
        : base($"VK API '{method}' вернул ошибку {errorCode}: {message}")
    {
        Method = method;
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Сессия недействительна: cookies больше не позволяют получить web-токен
/// (разлогинили, истёк remixsid и т.п.). Требуется повторный интерактивный вход.
/// </summary>
public sealed class VkSessionExpiredException : VkClientException
{
    public VkSessionExpiredException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Не удалось завершить интерактивный вход (таймаут, закрытое окно и т.п.).</summary>
public sealed class VkAuthenticationException : VkClientException
{
    public VkAuthenticationException(string message, Exception? inner = null) : base(message, inner) { }
}
