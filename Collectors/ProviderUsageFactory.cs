using AIUsageMonitor.Models;

namespace AIUsageMonitor.Collectors;

internal static class ProviderUsageFactory
{
    public static ProviderUsage Unavailable(string providerName, string message, string source = "", string planName = "")
    {
        return new ProviderUsage
        {
            Name = providerName,
            PlanName = planName,
            Source = source,
            IsUnavailable = true,
            StatusMessage = message,
            LastCheckedAt = DateTimeOffset.Now,
            Windows = []
        };
    }

    public static UsageWindow PercentWindow(
        string title,
        double usedPercent,
        DateTimeOffset? resetAt,
        string detail = "",
        string displayGroupName = "")
    {
        var used = Math.Clamp(usedPercent, 0, 100);

        return new UsageWindow
        {
            Title = title,
            DisplayGroupName = displayGroupName,
            Limit = 100,
            Used = used,
            Remaining = Math.Max(100 - used, 0),
            ResetAt = resetAt,
            Detail = detail
        };
    }

    public static UsageWindow InactiveWindow(string title, string detail = "Not on this plan")
    {
        return new UsageWindow
        {
            Title = title,
            Limit = 100,
            Used = 100,
            Remaining = 0,
            ResetAt = null,
            Detail = detail,
            IsInactive = true
        };
    }
}
