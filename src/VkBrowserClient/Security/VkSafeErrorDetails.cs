using System.Text.Json;

namespace VkBrowserClient;

/// <summary>
/// Extracts a small amount of non-sensitive structure from an upstream error.
/// Arbitrary text and values are never copied into the result: provider payloads can
/// contain cookies, tokens, signed upload URLs, hashes and streaming credentials.
/// </summary>
internal static class VkSafeErrorDetails
{
    internal const int MaxLength = 160;

    private static readonly HashSet<string> AllowedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "denied",
        "error",
        "expired",
        "failed",
        "failure",
        "forbidden",
        "invalid",
        "ok",
        "okay",
        "pending",
        "processing",
        "ready",
        "rejected",
        "success",
        "temporary",
        "unknown",
    };

    public static string Describe(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "upstream details redacted";

        try
        {
            using var document = JsonDocument.Parse(payload);
            return Describe(document.RootElement);
        }
        catch (JsonException)
        {
            // Plain text and HTML are deliberately not echoed. They are unstructured and
            // may include authorization data, a signed URL or account information.
            return "upstream details redacted";
        }
    }

    public static string Describe(JsonElement payload)
    {
        var facts = new List<string>(capacity: 3);
        AddFacts(payload, facts);

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object)
                AddFacts(error, facts);
            else if (error.ValueKind == JsonValueKind.Number && error.TryGetInt64(out var errorCode))
                AddFact(facts, $"code={errorCode}");
        }

        AddFact(facts, "upstream details redacted");
        var result = string.Join("; ", facts);
        return result.Length <= MaxLength ? result : result[..MaxLength];
    }

    private static void AddFacts(JsonElement value, ICollection<string> facts)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return;

        AddInteger(value, "error_code", "code", facts);
        AddInteger(value, "status_code", "status", facts);
        AddState(value, "type", facts);
        AddState(value, "status", facts);
    }

    private static void AddInteger(
        JsonElement value,
        string property,
        string label,
        ICollection<string> facts)
    {
        if (value.TryGetProperty(property, out var item) &&
            item.ValueKind == JsonValueKind.Number &&
            item.TryGetInt64(out var number))
        {
            AddFact(facts, $"{label}={number}");
        }
    }

    private static void AddState(JsonElement value, string property, ICollection<string> facts)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String)
            return;

        var state = item.GetString();
        if (state is not null && AllowedStates.Contains(state))
            AddFact(facts, $"{property}={state.ToLowerInvariant()}");
    }

    private static void AddFact(ICollection<string> facts, string fact)
    {
        if (!facts.Contains(fact))
            facts.Add(fact);
    }
}
