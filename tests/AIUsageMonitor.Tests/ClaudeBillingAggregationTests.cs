using System.IO;
using System.Text.Json;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ClaudeBillingAggregationTests
{
    private string _root = null!;
    private AppSettingsService _settings = null!;
    private ProviderAccount _account = null!;
    private AppLogService _log = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new AppSettingsService(_root);
        _log = new AppLogService(_root);
        _account = new ProviderAccount
        {
            Id = "anthropic-personal", ProviderName = KnownProviders.Anthropic, Label = "Personal", AccountUuid = "personal-id",
            ConfigDir = Path.Combine(_root, "account"), ClaudeBilling = new ClaudeBillingConnection
            {
                Id = Guid.NewGuid().ToString("N"), AccountUuid = "personal-id", OrganizationUuid = Guid.NewGuid().ToString(),
                State = ClaudeBillingConnectionState.Connected
            }
        };
        Directory.CreateDirectory(_account.ConfigDir);
        File.WriteAllText(ClaudeBillingService.CredentialsPath(_account), """{"claudeAiOauth":{"accessToken":"fake-token"}}""");
        _settings.Update(settings => settings.ProviderAccounts.Add(_account));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private UsageAggregatorService Aggregator(IClaudeBillingReader reader, UsageWindow? oauthBalance = null,
        ClaudeSlotIdentityService? slot = null) => new(_log, _settings,
        [new AccountScopedUsageCollector(new FakeQuota(oauthBalance), _account.DisplayKey, KnownProviders.Anthropic, _account.CardLabel)],
        slotIdentityService: slot, billingReader: reader);

    private static ClaudeBillingResult Balance(decimal amount = 113.85m) => new(
        ClaudeUsageCredits.CreateBalance(amount, "USD", 2, "Test balance"), ClaudeBillingConnectionState.Connected, "Connected");
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task QuotaPublishesBeforeDelayedBalanceAndKeepsSameAccountKey()
    {
        var started = Completion<bool>();
        var ready = Completion<ClaudeBillingResult>();
        var aggregator = Aggregator(new Reader(async (_, _, token) => { started.SetResult(true); return await ready.Task.WaitAsync(token); }));
        await using var stream = aggregator.CollectIncrementalAsync().GetAsyncEnumerator();
        Assert.IsTrue(await stream.MoveNextAsync());
        Assert.AreEqual(_account.DisplayKey, stream.Current.Name);
        Assert.IsFalse(stream.Current.Windows.Any(w => w.IsBalance));
        var next = stream.MoveNextAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(next.IsCompleted);
        ready.SetResult(Balance());
        Assert.IsTrue(await next);
        Assert.AreEqual(_account.DisplayKey, stream.Current.Name);
        Assert.AreEqual(_account.CardLabel, stream.Current.AccountLabel);
        Assert.AreEqual("$113.85 credits", stream.Current.Windows.Single(w => w.IsBalance).RemainingText);
        Assert.IsFalse(await stream.MoveNextAsync());
    }

    [TestMethod]
    [DataRow("remove")]
    [DataRow("disable")]
    [DataRow("identity")]
    [DataRow("disconnect")]
    [DataRow("reconnect")]
    [DataRow("credentials")]
    public async Task LateBalanceCannotRestoreRemovedChangedOrDisconnectedAccount(string change)
    {
        var started = Completion<bool>();
        var ready = Completion<ClaudeBillingResult>();
        var aggregator = Aggregator(new Reader(async (_, _, token) => { started.SetResult(true); return await ready.Task.WaitAsync(token); }));
        await using var stream = aggregator.CollectIncrementalAsync().GetAsyncEnumerator();
        Assert.IsTrue(await stream.MoveNextAsync());
        var next = stream.MoveNextAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _settings.Update(settings =>
        {
            var account = settings.ProviderAccounts.Single(a => a.Id == _account.Id);
            switch (change)
            {
                case "remove": settings.ProviderAccounts.Remove(account); break;
                case "disable": account.Enabled = false; break;
                case "identity": account.AccountUuid = "different-id"; break;
                case "disconnect": account.ClaudeBilling = null; break;
                case "reconnect": account.ClaudeBilling!.Id = Guid.NewGuid().ToString("N"); break;
                case "credentials": File.WriteAllText(ClaudeBillingService.CredentialsPath(account), """{"claudeAiOauth":{"accessToken":"changed"}}"""); break;
            }
        });
        ready.SetResult(Balance());
        Assert.IsFalse(await next, "The stale balance must never reach the overlay.");
    }

    [TestMethod]
    public async Task NewRefreshSupersedesOlderBalanceAndStatusUpdate()
    {
        var started = Completion<bool>();
        var ready = Completion<ClaudeBillingResult>();
        var calls = 0;
        var aggregator = Aggregator(new Reader(async (_, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { started.SetResult(true); return await ready.Task.WaitAsync(token); }
            return Balance(99m);
        }));
        await using var old = aggregator.CollectIncrementalAsync().GetAsyncEnumerator();
        Assert.IsTrue(await old.MoveNextAsync());
        var late = old.MoveNextAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var fresh = await aggregator.CollectAsync();
        Assert.AreEqual("$99.00 credits", fresh.Providers.Single().Windows.Single(w => w.IsBalance).RemainingText);
        ready.SetResult(new(null, ClaudeBillingConnectionState.ReconnectRequired, "old error"));
        Assert.IsFalse(await late);
        Assert.AreEqual(ClaudeBillingConnectionState.Connected,
            _settings.Load().ProviderAccounts.Single(a => a.Id == _account.Id).ClaudeBilling!.State);
    }

    [TestMethod]
    public async Task OAuthBalanceWinsWithoutCallingWebReader()
    {
        var aggregator = Aggregator(new Reader((_, _, _) => throw new AssertFailedException("OAuth already provided the balance.")), Balance().Balance);
        var snapshot = await aggregator.CollectAsync();
        Assert.AreEqual(1, snapshot.Providers.Single().Windows.Count(w => w.IsBalance));
    }

    [TestMethod]
    public async Task ExpiredBillingDoesNotMarkQuotaUnavailableAndRequiresReconnect()
    {
        var calls = 0;
        var aggregator = Aggregator(new Reader((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ClaudeBillingResult(null, ClaudeBillingConnectionState.ReconnectRequired, "Reconnect credits"));
        }));
        var snapshot = await aggregator.CollectAsync();
        Assert.IsFalse(snapshot.Providers.Single().IsUnavailable);
        Assert.AreEqual(1, snapshot.Providers.Single().Windows.Count);
        await aggregator.CollectAsync();
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task CancellationStopsWaitingWithoutPublishingBalanceOrErrorState()
    {
        var started = Completion<bool>();
        var aggregator = Aggregator(new Reader(async (_, _, token) =>
        {
            started.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return Balance();
        }));
        using var cancellation = new CancellationTokenSource();
        await using var stream = aggregator.CollectIncrementalAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await stream.MoveNextAsync());
        var next = stream.MoveNextAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => next);
        Assert.AreEqual(ClaudeBillingConnectionState.Connected,
            _settings.Load().ProviderAccounts.Single(a => a.Id == _account.Id).ClaudeBilling!.State);
    }

    [TestMethod]
    public async Task ActiveAccountUsesSlotCredentialsAndDropsResultIfSlotChanges()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        var slot = new ClaudeSlotIdentityService(_log, homeDirectory: home);
        File.WriteAllText(slot.CredentialsPath, """{"claudeAiOauth":{"accessToken":"slot-token"}}""");
        File.WriteAllText(slot.ClaudeJsonPath, JsonSerializer.Serialize(new { oauthAccount = new { accountUuid = _account.AccountUuid } }));
        var started = Completion<bool>();
        var ready = Completion<ClaudeBillingResult>();
        var aggregator = Aggregator(new Reader(async (_, path, token) =>
        {
            Assert.AreEqual(slot.CredentialsPath, path);
            started.SetResult(true);
            return await ready.Task.WaitAsync(token);
        }), slot: slot);
        await using var stream = aggregator.CollectIncrementalAsync().GetAsyncEnumerator();
        Assert.IsTrue(await stream.MoveNextAsync());
        var next = stream.MoveNextAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        File.WriteAllText(slot.CredentialsPath, """{"claudeAiOauth":{"accessToken":"other-account-token"}}""");
        ready.SetResult(Balance());
        Assert.IsFalse(await next);
    }

    private sealed class Reader(Func<ProviderAccount, string, CancellationToken, Task<ClaudeBillingResult>> read) : IClaudeBillingReader
    {
        public Task<ClaudeBillingResult> ReadAsync(ProviderAccount account, string path, CancellationToken cancellationToken) => read(account, path, cancellationToken);
    }
    private sealed class FakeQuota(UsageWindow? balance) : IUsageCollector
    {
        public string ProviderName => KnownProviders.Anthropic;
        public Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
        {
            var windows = new List<UsageWindow> { ProviderUsageFactory.PercentWindow("7d", 69, null) };
            if (balance is not null) windows.Add(balance);
            return Task.FromResult(new ProviderUsage { Name = ProviderName, Windows = windows });
        }
    }
}
