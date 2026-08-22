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
    private readonly ClaudeStatusFileUsageCollector? _defaultAnthropicCollector;
    private readonly ClaudeSlotIdentityService? _slotIdentityService;
    private readonly Dictionary<string, AccountCollectorEntry> _anthropicAccountCollectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _accountCollectorLock = new();

    public UsageAggregatorService(
        AppLogService logService,
        AppSettingsService settingsService,
        IReadOnlyList<IUsageCollector>? collectors = null,
        ClaudeSlotIdentityService? slotIdentityService = null)
    {
        _logService = logService;
        _settingsService = settingsService;

        if (collectors is not null)
        {
            _collectors = collectors;
            return;
        }

        _slotIdentityService = slotIdentityService ?? new ClaudeSlotIdentityService(
            logService,
            new AnthropicAccountManagerService(settingsService, logService));
        _defaultAnthropicCollector = new ClaudeStatusFileUsageCollector();
        _collectors =
        [
            _defaultAnthropicCollector,
            new AnthropicApiCreditsCollector(settingsService),
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

        // Settle which account ~/.claude is logged into before deciding which collectors
        // run, so a login changed outside the app is honored on this very tick.
        await SyncClaudeSlotIdentityAsync(cancellationToken).ConfigureAwait(false);

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
        return ResolveCollectors(settings)
            .Where(collector => settings.IsProviderEnabled(GetBaseProviderName(collector)));
    }

    private static string GetBaseProviderName(IUsageCollector collector)
    {
        return collector is AccountScopedUsageCollector accountCollector
            ? accountCollector.BaseProviderName
            : collector.ProviderName;
    }

    // Expands the default Anthropic collector into one collector per configured account.
    // Injected collector lists (tests, diagnostics fakes) bypass account resolution entirely.
    private IEnumerable<IUsageCollector> ResolveCollectors(AppSettings settings)
    {
        foreach (var collector in _collectors)
        {
            if (_defaultAnthropicCollector is not null && ReferenceEquals(collector, _defaultAnthropicCollector))
            {
                foreach (var accountCollector in ResolveAnthropicAccountCollectors(settings))
                {
                    yield return accountCollector;
                }
            }
            else
            {
                yield return collector;
            }
        }
    }

    private IEnumerable<IUsageCollector> ResolveAnthropicAccountCollectors(AppSettings settings)
    {
        var accounts = settings.GetAccounts(KnownProviders.Anthropic);
        PruneRemovedAccountCollectors(accounts);

        var slotAccountUuid = ResolveSlotAccountUuid(accounts);

        // When a managed account is the one currently logged into ~/.claude, the default
        // collector represents THAT account: it carries the managed account's key (so its
        // card keeps the account's name) and the managed dir's own collector is skipped
        // (its card would just duplicate the slot with staler tokens).
        var activeManagedAccount = string.IsNullOrWhiteSpace(slotAccountUuid)
            ? null
            : accounts.FirstOrDefault(account => !account.IsDefault &&
                string.Equals(account.AccountUuid, slotAccountUuid, StringComparison.OrdinalIgnoreCase));

        // Two collectors sharing a key would clobber each other's card on every upsert, so
        // a key is only ever handed out once no matter how the accounts are configured.
        var yieldedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in accounts)
        {
            if (account.IsDefault)
            {
                var effectiveAccount = activeManagedAccount ?? account;
                if (!effectiveAccount.Enabled || !yieldedKeys.Add(effectiveAccount.DisplayKey))
                {
                    continue;
                }

                // The wrapper is stateless, so the inner collector's backoff state is shared.
                yield return string.Equals(effectiveAccount.DisplayKey, KnownProviders.Anthropic, StringComparison.OrdinalIgnoreCase)
                    ? _defaultAnthropicCollector!
                    : new AccountScopedUsageCollector(_defaultAnthropicCollector!, effectiveAccount.DisplayKey, KnownProviders.Anthropic);
            }
            else if (account.Enabled &&
                !ReferenceEquals(account, activeManagedAccount) &&
                !string.IsNullOrWhiteSpace(account.ConfigDir) &&
                yieldedKeys.Add(account.DisplayKey))
            {
                yield return GetOrCreateAccountCollector(account);
            }
        }
    }

    // Which account owns the ~/.claude slot is detected from the slot itself, never from
    // whatever uuid happened to be stored last: the user can re-login it from the CLI, the
    // VS Code extension, or Claude Code without the app being told. A stale answer here
    // labels the slot card with the wrong account AND skips the account that really moved,
    // which looks exactly like two accounts reporting identical usage.
    private string ResolveSlotAccountUuid(IReadOnlyList<ProviderAccount> accounts)
    {
        if (_slotIdentityService is not null)
        {
            var identity = _slotIdentityService.GetIdentity();
            if (identity is not null && !string.IsNullOrWhiteSpace(identity.Uuid))
            {
                return identity.Uuid;
            }

            // No login in the slot at all means no managed account can be the active one;
            // every account then collects from its own dir.
            if (!_slotIdentityService.HasLogin)
            {
                return string.Empty;
            }
        }

        // Slot is logged in but its identity could not be read (unreadable ~/.claude.json
        // and no network): the last known value is the best guess available.
        return accounts.FirstOrDefault(account => account.IsDefault)?.AccountUuid ?? string.Empty;
    }

    // Confirms the slot's identity against the profile endpoint, records it so the accounts
    // window and tray menu agree with the cards, and mirrors the slot's rotating tokens back
    // into the matching managed account dir.
    private async Task SyncClaudeSlotIdentityAsync(CancellationToken cancellationToken)
    {
        if (_slotIdentityService is null)
        {
            return;
        }

        ClaudeSlotIdentityService.SlotIdentity? identity;
        try
        {
            identity = await _slotIdentityService.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.Error("Anthropic", $"Could not determine which account ~/.claude is logged into: {ex.Message}");
            return;
        }

        if (identity is null || string.IsNullOrWhiteSpace(identity.Uuid))
        {
            return;
        }

        var settings = _settingsService.Load();
        var accounts = settings.GetAccounts(KnownProviders.Anthropic);
        var defaultAccount = accounts.FirstOrDefault(account => account.IsDefault);
        if (defaultAccount is null)
        {
            return;
        }

        var uuidChanged = !string.Equals(defaultAccount.AccountUuid, identity.Uuid, StringComparison.OrdinalIgnoreCase);
        if (uuidChanged ||
            !string.Equals(defaultAccount.Email, identity.Email, StringComparison.OrdinalIgnoreCase))
        {
            if (uuidChanged)
            {
                _logService.Info(
                    "Anthropic",
                    $"The Claude CLI account in ~/.claude is now {DescribeAccount(accounts, identity.Uuid, identity.Email)}" +
                    $" (was {DescribeAccount(accounts, defaultAccount.AccountUuid, defaultAccount.Email)}).");
            }

            defaultAccount.AccountUuid = identity.Uuid;
            defaultAccount.Email = identity.Email;
            _settingsService.Save(settings);
        }

        var activeManagedAccount = accounts.FirstOrDefault(account => !account.IsDefault &&
            string.Equals(account.AccountUuid, identity.Uuid, StringComparison.OrdinalIgnoreCase));
        if (activeManagedAccount is not null)
        {
            _slotIdentityService.TryMirrorSlotCredentials(activeManagedAccount);
        }
    }

    private static string DescribeAccount(IReadOnlyList<ProviderAccount> accounts, string uuid, string email)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return "unknown";
        }

        var match = accounts.FirstOrDefault(account => !account.IsDefault &&
            string.Equals(account.AccountUuid, uuid, StringComparison.OrdinalIgnoreCase));
        var describedEmail = string.IsNullOrWhiteSpace(email) ? uuid : email;
        return match is null ? describedEmail : $"'{match.Label}' ({describedEmail})";
    }

    private IUsageCollector GetOrCreateAccountCollector(ProviderAccount account)
    {
        // Cache per account id: rebuilding collectors every refresh would silently drop
        // their per-instance OAuth/CLI backoff state.
        var fingerprint = $"{account.ConfigDir}|{account.DisplayKey}";

        lock (_accountCollectorLock)
        {
            if (_anthropicAccountCollectors.TryGetValue(account.Id, out var entry) &&
                string.Equals(entry.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Collector;
            }

            var collector = new AccountScopedUsageCollector(
                new ClaudeStatusFileUsageCollector(
                    claudeConfigDirectory: account.ConfigDir,
                    allowCliRefresh: false,
                    tokenRefresher: new AnthropicOAuthTokenRefresher(logService: _logService)),
                account.DisplayKey,
                KnownProviders.Anthropic);
            _anthropicAccountCollectors[account.Id] = new AccountCollectorEntry(fingerprint, collector);
            return collector;
        }
    }

    private void PruneRemovedAccountCollectors(IReadOnlyList<ProviderAccount> accounts)
    {
        lock (_accountCollectorLock)
        {
            if (_anthropicAccountCollectors.Count == 0)
            {
                return;
            }

            var accountIds = accounts
                .Select(account => account.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var accountId in _anthropicAccountCollectors.Keys.ToList())
            {
                if (!accountIds.Contains(accountId))
                {
                    _anthropicAccountCollectors.Remove(accountId);
                }
            }
        }
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

    private sealed record AccountCollectorEntry(string Fingerprint, IUsageCollector Collector);
}
