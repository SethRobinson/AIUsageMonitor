namespace AIUsageMonitor.Models;

public sealed class ProviderUsage
{
    public string Name { get; init; } = string.Empty;

    public string SourceProviderName { get; init; } = string.Empty;

    public string PlanName { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = string.Empty;

    public bool IsUnavailable { get; init; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    public List<UsageWindow> Windows { get; init; } = [];
}
