using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AIUsageMonitor.Services;

public sealed class UsageAggregatorService
{
    private readonly IReadOnlyList<IUsageCollector> _collectors;
    private readonly AppLogService _logService;
    private readonly AppSettingsService _settingsService;
    private readonly Dictionary<string, CollectorFailureState> _failureStates = new(StringComparer.OrdinalIgnoreCase);

    public UsageAggregatorService(AppLogService logService, AppSettingsService settingsService)
    {
        _logService = logService;
        _settingsService = settingsService;
        _collectors =
        [
            new ClaudeStatusFileUsageCollector(),
            new CodexLogUsageCollector(),
            new AntigravityUsageCollector(),
            new GeminiUsageCollector(),
            new CursorUsageCollector(settingsService)
        ];
    }

    public IReadOnlyList<string> ProviderNames => GetEnabledCollectors(_settingsService.Load())
        .Select(collector => collector.ProviderName)
        .ToList();

    public void ResetBackoff()
    {
        _failureStates.Clear();
    }

    public async Task<UsageSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var providers = new List<ProviderUsage>();
        var settings = _settingsService.Load();
        var enabledCollectors = GetEnabledCollectors(settings).ToList();
        RemoveDisabledFailureStates(enabledCollectors);

        foreach (var collector in enabledCollectors)
        {
            if (_failureStates.TryGetValue(collector.ProviderName, out var state) && state.NextRetryAt > now)
            {
                var backoffProvider = ProviderUsageFactory.Unavailable(
                    collector.ProviderName,
                    $"Collection paused until {state.NextRetryAt.ToLocalTime():h:mm tt} after the last error.",
                    "Backoff");
                backoffProvider.LastCheckedAt = state.LastAttemptAt;
                providers.Add(backoffProvider);
                continue;
            }

            try
            {
                var provider = await collector.CollectAsync(cancellationToken);
                provider.LastCheckedAt = DateTimeOffset.Now;
                providers.Add(provider);

                if (!provider.IsUnavailable)
                {
                    _failureStates.Remove(collector.ProviderName);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                var nextRetryAt = RecordFailure(collector.ProviderName, now, ex);
                var failedProvider = ProviderUsageFactory.Unavailable(
                    collector.ProviderName,
                    $"Collection failed. Next retry after {nextRetryAt.ToLocalTime():h:mm tt}.",
                    "Error");
                failedProvider.LastCheckedAt = now;
                providers.Add(failedProvider);
            }
        }

        return new UsageSnapshot
        {
            GeneratedAt = now,
            Source = "Live/local collectors",
            Providers = providers
        };
    }

    private IEnumerable<IUsageCollector> GetEnabledCollectors(AppSettings settings)
    {
        return _collectors.Where(collector => settings.IsProviderEnabled(collector.ProviderName));
    }

    private void RemoveDisabledFailureStates(IReadOnlyCollection<IUsageCollector> enabledCollectors)
    {
        var enabledProviderNames = enabledCollectors
            .Select(collector => collector.ProviderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var providerName in _failureStates.Keys.ToList())
        {
            if (!enabledProviderNames.Contains(providerName))
            {
                _failureStates.Remove(providerName);
            }
        }
    }

    private DateTimeOffset RecordFailure(string providerName, DateTimeOffset now, Exception ex)
    {
        _failureStates.TryGetValue(providerName, out var state);
        var failures = (state?.FailureCount ?? 0) + 1;
        var delayMinutes = Math.Min(60, Math.Pow(2, Math.Min(failures - 1, 5)) * 5);
        var nextRetryAt = now.AddMinutes(delayMinutes);

        _failureStates[providerName] = new CollectorFailureState(failures, nextRetryAt, now);
        _logService.Error(providerName, $"{ex.GetType().Name}: {ex.Message}. Backing off for {delayMinutes:0} minutes.");
        return nextRetryAt;
    }

    private sealed record CollectorFailureState(int FailureCount, DateTimeOffset NextRetryAt, DateTimeOffset LastAttemptAt);
}
