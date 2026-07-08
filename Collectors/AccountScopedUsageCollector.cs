using AIUsageMonitor.Models;

namespace AIUsageMonitor.Collectors;

// Wraps a single-account collector so each configured account gets its own provider key
// (e.g. "Anthropic - Work"). The unique key flows through the aggregator failure states,
// checking placeholders, and card upserts unchanged; results are re-stamped so the card
// layer groups them under the account instead of the base provider.
public sealed class AccountScopedUsageCollector : IForceRefreshUsageCollector
{
    private readonly IUsageCollector _inner;

    public AccountScopedUsageCollector(IUsageCollector inner, string accountKey, string baseProviderName)
    {
        _inner = inner;
        ProviderName = accountKey;
        BaseProviderName = baseProviderName;
    }

    public string ProviderName { get; }

    public string BaseProviderName { get; }

    public async Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
    {
        return await CollectAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderUsage> CollectAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var usage = _inner is IForceRefreshUsageCollector forceRefreshCollector
            ? await forceRefreshCollector.CollectAsync(forceRefresh, cancellationToken).ConfigureAwait(false)
            : await _inner.CollectAsync(cancellationToken).ConfigureAwait(false);

        return new ProviderUsage
        {
            Name = ProviderName,
            SourceProviderName = ProviderName,
            PlanName = usage.PlanName,
            Source = usage.Source,
            StatusMessage = usage.StatusMessage,
            IsUnavailable = usage.IsUnavailable,
            LastCheckedAt = usage.LastCheckedAt,
            Windows = usage.Windows
        };
    }
}
