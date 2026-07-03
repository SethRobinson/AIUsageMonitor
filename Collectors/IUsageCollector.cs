using AIUsageMonitor.Models;

namespace AIUsageMonitor.Collectors;

public interface IUsageCollector
{
    string ProviderName { get; }

    Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken);
}

public interface IForceRefreshUsageCollector : IUsageCollector
{
    Task<ProviderUsage> CollectAsync(bool forceRefresh, CancellationToken cancellationToken);
}
