using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
    private readonly object _failureStateLock = new();

    public UsageAggregatorService(
        AppLogService logService,
        AppSettingsService settingsService,
        IReadOnlyList<IUsageCollector>? collectors = null)
    {
        _logService = logService;
        _settingsService = settingsService;
        _collectors = collectors ??
        [
            new ClaudeStatusFileUsageCollector(),
            new CodexLogUsageCollector(logService, settingsService),
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
        lock (_failureStateLock)
        {
            _failureStates.Clear();
        }
    }

    public async Task<UsageSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        return await CollectAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageSnapshot> CollectAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var providerNames = ProviderNames;
        var providersByName = new ConcurrentDictionary<string, ProviderUsage>(StringComparer.OrdinalIgnoreCase);

        await foreach (var provider in CollectIncrementalAsync(forceRefresh, cancellationToken).ConfigureAwait(false))
        {
            providersByName[provider.Name] = provider;
        }

        var providers = providerNames
            .Select(providerName => providersByName.TryGetValue(providerName, out var provider) ? provider : null)
            .Where(provider => provider is not null)
            .Cast<ProviderUsage>()
            .ToList();

        foreach (var provider in providersByName.Values.Where(provider =>
            !providerNames.Contains(provider.Name, StringComparer.OrdinalIgnoreCase)))
        {
            providers.Add(provider);
        }

        return new UsageSnapshot
        {
            GeneratedAt = DateTimeOffset.Now,
            Source = "Live/local collectors",
            Providers = providers
        };
    }

    public async IAsyncEnumerable<ProviderUsage> CollectIncrementalAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var provider in CollectIncrementalAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false))
        {
            yield return provider;
        }
    }

    public async IAsyncEnumerable<ProviderUsage> CollectIncrementalAsync(
        bool forceRefresh,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _settingsService.Load();
        var enabledCollectors = GetEnabledCollectors(settings).ToList();
        RemoveDisabledFailureStates(enabledCollectors);

        var pendingTasks = enabledCollectors
            .Select(collector => Task.Run(
                () => CollectProviderAsync(collector, forceRefresh, cancellationToken),
                CancellationToken.None))
            .ToList();
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        while (pendingTasks.Count > 0)
        {
            var completedTask = await Task
                .WhenAny(pendingTasks.Cast<Task>().Append(cancellationTask))
                .ConfigureAwait(false);

            if (ReferenceEquals(completedTask, cancellationTask))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var providerTask = (Task<ProviderUsage>)completedTask;
            pendingTasks.Remove(providerTask);

            yield return await providerTask.ConfigureAwait(false);
        }
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

        lock (_failureStateLock)
        {
            foreach (var providerName in _failureStates.Keys.ToList())
            {
                if (!enabledProviderNames.Contains(providerName))
                {
                    _failureStates.Remove(providerName);
                }
            }
        }
    }

    private async Task<ProviderUsage> CollectProviderAsync(
        IUsageCollector collector,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (TryCreateBackoffProvider(collector.ProviderName, now, out var backoffProvider))
        {
            return backoffProvider;
        }

        try
        {
            var provider = collector is IForceRefreshUsageCollector forceRefreshCollector
                ? await forceRefreshCollector.CollectAsync(forceRefresh, cancellationToken).ConfigureAwait(false)
                : await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            provider.LastCheckedAt = DateTimeOffset.Now;
            if (!provider.IsUnavailable)
            {
                ClearFailure(collector.ProviderName);
            }

            return provider;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nextRetryAt = RecordFailure(collector.ProviderName, now, ex);
            var failedProvider = ProviderUsageFactory.Unavailable(
                collector.ProviderName,
                $"Collection failed. Next retry after {nextRetryAt.ToLocalTime():h:mm tt}.",
                "Error");
            failedProvider.LastCheckedAt = now;
            return failedProvider;
        }
    }

    private bool TryCreateBackoffProvider(string providerName, DateTimeOffset now, out ProviderUsage provider)
    {
        lock (_failureStateLock)
        {
            if (_failureStates.TryGetValue(providerName, out var state) && state.NextRetryAt > now)
            {
                provider = ProviderUsageFactory.Unavailable(
                    providerName,
                    $"Collection paused until {state.NextRetryAt.ToLocalTime():h:mm tt} after the last error.",
                    "Backoff");
                provider.LastCheckedAt = state.LastAttemptAt;
                return true;
            }
        }

        provider = null!;
        return false;
    }

    private void ClearFailure(string providerName)
    {
        lock (_failureStateLock)
        {
            _failureStates.Remove(providerName);
        }
    }

    private DateTimeOffset RecordFailure(string providerName, DateTimeOffset now, Exception ex)
    {
        DateTimeOffset nextRetryAt;
        double delayMinutes;

        lock (_failureStateLock)
        {
            _failureStates.TryGetValue(providerName, out var state);
            var failures = (state?.FailureCount ?? 0) + 1;
            delayMinutes = Math.Min(60, Math.Pow(2, Math.Min(failures - 1, 5)) * 5);
            nextRetryAt = now.AddMinutes(delayMinutes);

            _failureStates[providerName] = new CollectorFailureState(failures, nextRetryAt, now);
        }

        _logService.Error(providerName, $"{ex.GetType().Name}: {ex.Message}. Backing off for {delayMinutes:0} minutes.");
        return nextRetryAt;
    }

    private sealed record CollectorFailureState(int FailureCount, DateTimeOffset NextRetryAt, DateTimeOffset LastAttemptAt);
}
