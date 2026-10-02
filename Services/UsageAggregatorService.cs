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
    private readonly Dictionary<string, AccountCollectorEntry> _openAiAccountCollectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<ProviderAccount, IUsageCollector> _openAiCollectorFactory;
    private readonly IClaudeBillingReader _billingReader;
    private long _refreshGeneration;

    public UsageAggregatorService(
        AppLogService logService,
        AppSettingsService settingsService,
        IReadOnlyList<IUsageCollector>? collectors = null,
        ClaudeSlotIdentityService? slotIdentityService = null,
        Func<ProviderAccount, IUsageCollector>? openAiCollectorFactory = null,
        IClaudeBillingReader? billingReader = null)
    {
        _logService = logService;
        _settingsService = settingsService;
        _billingReader = billingReader ?? new ClaudeBillingService(settingsService);
        _slotIdentityService = slotIdentityService;
        _openAiCollectorFactory = openAiCollectorFactory ?? (account => new OpenAiAccountUsageCollector(account));

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
        var generation = Interlocked.Increment(ref _refreshGeneration);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var refreshToken = lifetime.Token;

        // Settle which account ~/.claude is logged into before deciding which collectors
        // run, so a login changed outside the app is honored on this very tick.
        await SyncClaudeSlotIdentityAsync(cancellationToken).ConfigureAwait(false);

        var settings = _settingsService.Load();
        var enabledCollectors = GetEnabledCollectors(settings).ToList();
        RemoveDisabledFailureStates(enabledCollectors);

        var billingAccounts = settings.GetMonitoringAccounts(KnownProviders.Anthropic)
            .Where(account => account.Enabled && account.ClaudeBilling is not null)
            .ToDictionary(account => account.DisplayKey, account => account.Clone(), StringComparer.OrdinalIgnoreCase);
        var slotUuid = billingAccounts.Count > 0 ? ResolveSlotAccountUuid(settings.GetAccounts(KnownProviders.Anthropic)) : string.Empty;
        var pendingTasks = enabledCollectors
            .Select(collector => Task.Run(async () =>
                (ProviderUsage?)await CollectProviderAsync(collector, forceRefresh, refreshToken).ConfigureAwait(false),
                CancellationToken.None))
            .ToList();
        var billingTasks = new HashSet<Task<ProviderUsage?>>();
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        try
        {
            while (pendingTasks.Count > 0)
            {
                var completedTask = await Task
                    .WhenAny(pendingTasks.Cast<Task>().Append(cancellationTask))
                    .ConfigureAwait(false);
                if (ReferenceEquals(completedTask, cancellationTask)) throw new OperationCanceledException(cancellationToken);
                if (generation != Volatile.Read(ref _refreshGeneration)) yield break;

                var providerTask = (Task<ProviderUsage?>)completedTask;
                pendingTasks.Remove(providerTask);
                var isBilling = billingTasks.Remove(providerTask);
                var provider = await providerTask.ConfigureAwait(false);
                if (provider is null) continue;
                yield return provider;

                // Quotas reach the UI first. Optional billing is a second update under the
                // same DisplayKey, never another card or a dependency of any quota task.
                if (!isBilling && !provider.IsUnavailable && !provider.Windows.Any(window => window.IsBalance) &&
                    billingAccounts.TryGetValue(provider.Name, out var account) &&
                    account.ClaudeBilling!.State != ClaudeBillingConnectionState.ReconnectRequired &&
                    (forceRefresh || account.ClaudeBilling.RetryAfter is null || account.ClaudeBilling.RetryAfter <= DateTimeOffset.Now))
                {
                    var path = (account.IsDefault || account.AccountUuid == slotUuid) && _slotIdentityService is not null
                        ? _slotIdentityService.CredentialsPath : ClaudeBillingService.CredentialsPath(account);
                    var task = Task.Run(() => EnrichBillingAsync(account, path, provider, generation, refreshToken), CancellationToken.None);
                    pendingTasks.Add(task);
                    billingTasks.Add(task);
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            // Observe workers abandoned by cancellation or a consumer disposing the stream.
            foreach (var task in pendingTasks)
                _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private async Task<ProviderUsage?> EnrichBillingAsync(ProviderAccount account, string path, ProviderUsage quota,
        long generation, CancellationToken cancellationToken)
    {
        var fingerprint = ClaudeBillingService.CredentialFingerprint(path);
        var result = await _billingReader.ReadAsync(account, path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _refreshGeneration) || fingerprint != ClaudeBillingService.CredentialFingerprint(path)) return null;
        var accepted = false;
        _settingsService.Update(settings =>
        {
            var current = settings.GetMonitoringAccounts(KnownProviders.Anthropic).FirstOrDefault(a => a.Id == account.Id);
            if (generation != Volatile.Read(ref _refreshGeneration) || !settings.IsProviderEnabled(KnownProviders.Anthropic) ||
                current is null || !current.Enabled || current.DisplayKey != account.DisplayKey || current.AccountUuid != account.AccountUuid ||
                current.ConfigDir != account.ConfigDir || current.ClaudeBilling?.Id != account.ClaudeBilling?.Id) return;
            current.ClaudeBilling!.State = result.State;
            current.ClaudeBilling.Message = result.Message;
            current.ClaudeBilling.LastCheckedAt = DateTimeOffset.Now;
            current.ClaudeBilling.RetryAfter = result.State == ClaudeBillingConnectionState.TemporarilyUnavailable
                ? DateTimeOffset.Now.AddMinutes(10) : null;
            accepted = true;
        });
        if (!accepted) return null;
        return new ProviderUsage
        {
            Name = quota.Name, SourceProviderName = quota.SourceProviderName, AccountLabel = quota.AccountLabel,
            PlanName = quota.PlanName, Source = quota.Source, IsUnavailable = quota.IsUnavailable, LastCheckedAt = quota.LastCheckedAt,
            StatusMessage = result.Balance is null ? quota.StatusMessage + " " + result.Message : quota.StatusMessage,
            Windows = result.Balance is null ? quota.Windows : [.. quota.Windows, result.Balance]
        };
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
            else if (collector is CodexLogUsageCollector && settings.OpenAiUseSavedAccounts)
            {
                foreach (var accountCollector in ResolveOpenAiAccountCollectors(settings))
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

    private IReadOnlyList<IUsageCollector> ResolveOpenAiAccountCollectors(AppSettings settings)
    {
        var accounts = settings.GetAccounts(KnownProviders.OpenAI).Where(account => !account.IsDefault).ToList();
        var result = new List<IUsageCollector>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_accountCollectorLock)
        {
            foreach (var removed in _openAiAccountCollectors.Keys.Except(accounts.Select(account => account.Id), StringComparer.OrdinalIgnoreCase).ToList())
                _openAiAccountCollectors.Remove(removed);
            foreach (var account in accounts.Where(account => account.Enabled && !string.IsNullOrWhiteSpace(account.ConfigDir)))
            {
                if (!keys.Add(account.DisplayKey)) continue;
                var fingerprint = $"{account.ConfigDir}|{account.DisplayKey}|{account.AccountUuid}";
                if (!_openAiAccountCollectors.TryGetValue(account.Id, out var entry) || entry.Fingerprint != fingerprint)
                {
                    entry = new AccountCollectorEntry(fingerprint, new AccountScopedUsageCollector(
                        _openAiCollectorFactory(account), account.DisplayKey, KnownProviders.OpenAI, account.CardLabel));
                    _openAiAccountCollectors[account.Id] = entry;
                }
                result.Add(entry.Collector);
            }
        }
        return result;
    }

    private IEnumerable<IUsageCollector> ResolveAnthropicAccountCollectors(AppSettings settings)
    {
        var accounts = settings.GetAccounts(KnownProviders.Anthropic);
        PruneRemovedAccountCollectors(accounts);

        var slotAccountUuid = ResolveSlotAccountUuid(accounts);

        // A saved account using ~/.claude can still read its freshest tokens from that
        // slot. It remains one saved account, never an additional Default card.
        var activeManagedAccount = string.IsNullOrWhiteSpace(slotAccountUuid)
            ? null
            : accounts.FirstOrDefault(account => !account.IsDefault &&
                string.Equals(account.AccountUuid, slotAccountUuid, StringComparison.OrdinalIgnoreCase));

        // Two collectors sharing a key would clobber each other's card on every upsert, so
        // a key is only ever handed out once no matter how the accounts are configured.
        var yieldedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in settings.GetMonitoringAccounts(KnownProviders.Anthropic))
        {
            if (!account.Enabled || !yieldedKeys.Add(account.DisplayKey))
            {
                continue;
            }

            if (account.IsDefault || ReferenceEquals(account, activeManagedAccount))
            {
                // The wrapper is stateless, so the inner collector's backoff state is shared.
                yield return string.Equals(account.DisplayKey, KnownProviders.Anthropic, StringComparison.OrdinalIgnoreCase)
                    ? _defaultAnthropicCollector!
                    : new AccountScopedUsageCollector(_defaultAnthropicCollector!, account.DisplayKey, KnownProviders.Anthropic, account.CardLabel);
            }
            else if (!string.IsNullOrWhiteSpace(account.ConfigDir))
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
            _settingsService.Update(current =>
            {
                var slot = current.ProviderAccounts.FirstOrDefault(account => account.IsDefault && account.ProviderName == KnownProviders.Anthropic);
                if (slot is not null) { slot.AccountUuid = identity.Uuid; slot.Email = identity.Email; }
            });
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
                KnownProviders.Anthropic,
                account.CardLabel);
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
