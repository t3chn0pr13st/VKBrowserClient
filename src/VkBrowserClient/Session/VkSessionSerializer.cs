using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Портируемая (де)сериализация сессии — например, чтобы передать её на сервер
/// через переменную окружения или секрет-менеджер (base64), без файла.
/// </summary>
public static class VkSessionSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Сериализовать сессию в base64-строку.</summary>
    public static string ToBase64(VkSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(session, Options));
    }

    /// <summary>Восстановить сессию из base64-строки.</summary>
    public static VkSession FromBase64(string base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64);
        var session = JsonSerializer.Deserialize<VkSession>(Convert.FromBase64String(base64), Options)
                      ?? throw new VkClientException("Не удалось разобрать сессию из base64.");
        session.Cookies ??= new List<VkCookie>();
        return session;
    }
}
