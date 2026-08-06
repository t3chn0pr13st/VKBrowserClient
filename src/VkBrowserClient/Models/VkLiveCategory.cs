namespace VkBrowserClient;

/// <summary>Категория прямых трансляций VK; список может быть вложенным.</summary>
public sealed record VkLiveCategory
{
    public required int Id { get; init; }
    public required string Label { get; init; }
    public IReadOnlyList<VkLiveCategory> Children { get; init; } = [];
}
