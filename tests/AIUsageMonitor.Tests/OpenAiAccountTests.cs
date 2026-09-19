using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class OpenAiAccountTests
{
    [TestMethod]
    public async Task SavedAccountsHaveSeparateCredentialsAndSurviveAutomaticLoginChanges()
    {
        using var context = new Context();
        var personal = await context.Add("Personal", "alice", 25);
        var work = await context.Add("Work", "bob", 65);
        Assert.AreNotEqual(personal.ConfigDir, work.ConfigDir);
        var client = new FakeClient();
        client.ReadHandler = (dir, _) => Task.FromResult(Snapshot(dir == personal.ConfigDir ? 25 : 65));
        var first = new OpenAiAccountUsageCollector(personal, client);
        var second = new OpenAiAccountUsageCollector(work, client);
        WriteAuth(Path.Combine(context.Root, "normal-codex"), "unrelated", "workspace");
        Assert.AreEqual(75, (await first.CollectAsync(default)).Windows[0].Remaining);
        Assert.AreEqual(35, (await second.CollectAsync(default)).Windows[0].Remaining);
        WriteAuth(Path.Combine(context.Root, "normal-codex"), "switched", "workspace");
        Assert.AreEqual(75, (await first.CollectAsync(default)).Windows[0].Remaining);
        Assert.IsTrue(client.ReadDirectories.All(dir => dir == personal.ConfigDir || dir == work.ConfigDir));
    }

    [TestMethod]
    public async Task WrongAccountDuplicateAndCancelledLoginPreserveCredentials()
    {
        using var context = new Context();
        var account = await context.Add("Personal", "alice", 25);
        var original = File.ReadAllText(Path.Combine(account.ConfigDir, "auth.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Add("Duplicate", "alice", 25));
        context.Client.User = "bob";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Manager.LoginAsync("Personal", account.Id, _ => { }, default));
        Assert.AreEqual(original, File.ReadAllText(Path.Combine(account.ConfigDir, "auth.json")));
        context.Client.LoginHandler = async (_, token) => await Task.Delay(Timeout.Infinite, token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Manager.LoginAsync("Personal", account.Id, _ => { }, cancellation.Token));
        Assert.AreEqual(original, File.ReadAllText(Path.Combine(account.ConfigDir, "auth.json")));
        Assert.HasCount(1, context.Settings.Load().GetAccounts(KnownProviders.OpenAI));
        Assert.IsEmpty(Directory.GetDirectories(context.Manager.AccountsRootDirectory, "pending-*"));
    }

    [TestMethod]
    public async Task ReloginRotatesOnlyTheExistingSavedAccount()
    {
        using var context = new Context();
        var account = await context.Add("Personal", "alice", 25);
        context.Client.TokenMarker = "rotated-fake-token";
        var reconnected = await context.Manager.LoginAsync("Personal", account.Id, _ => { }, default);
        Assert.AreEqual(account.Id, reconnected.Id);
        StringAssert.Contains(File.ReadAllText(Path.Combine(account.ConfigDir, "auth.json")), "rotated-fake-token");
        Assert.HasCount(1, context.Settings.Load().GetAccounts(KnownProviders.OpenAI));
    }

    [TestMethod]
    public async Task MigrationAndCachedSettingsPreserveSavedModeEvenAfterRemovingLastAccount()
    {
        using var context = new Context();
        var cached = context.Settings.Load();
        Assert.IsFalse(cached.OpenAiUseSavedAccounts);
        var account = await context.Add("Personal", "alice", 25);
        cached.UpdateIntervalMinutes = 13;
        context.Settings.SavePreservingAccounts(cached);
        var saved = context.Settings.Load();
        Assert.IsTrue(saved.OpenAiUseSavedAccounts);
        Assert.IsTrue(saved.Clone().OpenAiUseSavedAccounts);
        Assert.AreEqual(13, saved.UpdateIntervalMinutes);
        Assert.HasCount(1, saved.GetAccounts(KnownProviders.OpenAI));
        context.Manager.SetEnabled(account.Id, false);
        Assert.IsTrue(context.Settings.Load().OpenAiUseSavedAccounts);
        await context.Manager.RemoveAsync(account.Id);
        Assert.IsFalse(Directory.Exists(account.ConfigDir));
        Assert.IsTrue(context.Settings.Load().OpenAiUseSavedAccounts);
        Assert.IsEmpty(context.Settings.Load().GetAccounts(KnownProviders.OpenAI));
    }

    [TestMethod]
    public async Task RemovalRejectsSiblingFoldersAndKeepsOtherAccounts()
    {
        using var context = new Context();
        var account = await context.Add("Personal", "alice", 25);
        var other = await context.Add("Work", "bob", 35);
        var outside = context.Manager.AccountsRootDirectory + "-outside";
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        context.Settings.Update(settings => settings.ProviderAccounts.Single(item => item.Id == account.Id).ConfigDir = outside);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Manager.RemoveAsync(account.Id));
        Assert.IsTrue(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(other.ConfigDir, "auth.json")));
    }

    [TestMethod]
    public async Task IdentityMismatchNeverCallsUsageClient()
    {
        using var context = new Context();
        var account = await context.Add("Personal", "alice", 25);
        WriteAuth(account.ConfigDir, "different-user", "workspace");
        var client = new FakeClient();
        var result = await new OpenAiAccountUsageCollector(account, client).CollectAsync(default);
        Assert.IsTrue(result.IsUnavailable);
        Assert.IsEmpty(client.ReadDirectories);
    }

    [TestMethod]
    public async Task TimeoutsBackoffForceRefreshAndCancellationArePerAccount()
    {
        using var context = new Context();
        var account = await context.Add("Personal", "alice", 25);
        var client = new FakeClient { ReadHandler = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Snapshot(10); } };
        var collector = new OpenAiAccountUsageCollector(account, client, TimeSpan.FromMilliseconds(80));
        Assert.IsTrue((await collector.CollectAsync(default)).IsUnavailable);
        Assert.IsTrue((await collector.CollectAsync(default)).IsUnavailable);
        Assert.HasCount(1, client.ReadDirectories);
        client.ReadHandler = (_, _) => Task.FromResult(Snapshot(40));
        Assert.AreEqual(60, (await collector.CollectAsync(true, default)).Windows[0].Remaining);
        client.ReadHandler = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Snapshot(10); };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<OperationCanceledException>(() => collector.CollectAsync(true, cancellation.Token));
    }

    [TestMethod]
    public async Task OpenAiAggregationExpandsCachesRenamesDisablesAndCompletesIndependently()
    {
        using var context = new Context();
        var first = await context.Add("Fast", "alice", 10);
        var second = await context.Add("Slow", "bob", 30);
        var created = 0;
        var log = new AppLogService(context.Root);
        var aggregator = new UsageAggregatorService(log, context.Settings,
            collectors: [new CodexLogUsageCollector()], openAiCollectorFactory: account =>
            {
                created++;
                return new DelayedCollector(account.Id == first.Id ? 5 : 200, account.Id == first.Id ? 10 : 30);
            });
        CollectionAssert.AreEquivalent(new[] { first.DisplayKey, second.DisplayKey }, aggregator.ProviderNames.ToArray());
        var results = new List<ProviderUsage>();
        await foreach (var usage in aggregator.CollectIncrementalAsync()) results.Add(usage);
        Assert.AreEqual(first.DisplayKey, results[0].Name);
        Assert.AreEqual("Fast", results[0].AccountLabel);
        Assert.AreEqual(90, results[0].Windows[0].Remaining);
        Assert.AreEqual(70, results[1].Windows[0].Remaining);
        Assert.AreEqual(2, created);
        _ = aggregator.ProviderNames;
        Assert.AreEqual(2, created);
        context.Manager.Rename(first.Id, "Seth - Personal");
        CollectionAssert.Contains(aggregator.ProviderNames.ToArray(), "OpenAI - Seth - Personal");
        Assert.AreEqual(3, created);
        context.Manager.SetEnabled(second.Id, false);
        Assert.HasCount(1, aggregator.ProviderNames);
        context.Settings.Update(settings => settings.SetProviderEnabled(KnownProviders.OpenAI, false));
        Assert.IsEmpty(aggregator.ProviderNames);
        context.Settings.Update(settings => settings.SetProviderEnabled(KnownProviders.OpenAI, true));
        context.Manager.SetEnabled(first.Id, false);
        Assert.IsEmpty(aggregator.ProviderNames);
    }

    [TestMethod]
    public void RateLimitParsingPrefersCodexBucketAndDoesNotInventMissingWindows()
    {
        var result = OpenAiAccountUsageCollector.ParseUsage(new CodexAccountSnapshot("fake@example.com", "plus", Json("""
            {"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{
              "other":{"primary":{"usedPercent":100}},
              "codex":{"primary":{"usedPercent":35,"windowDurationMins":300,"resetsAt":1900000000},"secondary":null}}}
            """)));
        Assert.AreEqual(65, result.Windows.Single().Remaining);
        Assert.AreEqual("5h", result.Windows.Single().Title);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1900000000), result.Windows.Single().ResetAt);
        Assert.AreEqual("Plus", result.PlanName);
        var missing = OpenAiAccountUsageCollector.ParseUsage(new CodexAccountSnapshot("", "", Json("""{"rateLimits":{"primary":{"usedPercent":null},"secondary":null}}""")));
        Assert.IsTrue(missing.IsUnavailable);
        Assert.IsEmpty(missing.Windows);
        Assert.AreEqual(70, OpenAiAccountUsageCollector.ParseUsage(Snapshot(30)).Windows.Single().Remaining);
    }

    [TestMethod]
    public void PurchasedCreditsBecomeABalanceRowNextToTheQuotaWindows()
    {
        var result = OpenAiAccountUsageCollector.ParseUsage(new CodexAccountSnapshot("fake@example.com", "plus", Json("""
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":1900000000},
              "credits":{"hasCredits":true,"unlimited":false,"balance":"2388.6764000000"},"planType":"pro"}}}
            """)));
        Assert.IsFalse(result.IsUnavailable);
        Assert.HasCount(2, result.Windows);
        var credits = result.Windows.Single(window => window.Title == CodexCredits.WindowTitle);
        Assert.IsTrue(credits.IsBalance);
        Assert.IsTrue(credits.HideReset);
        Assert.AreEqual("2,389 credits", credits.RemainingText);
        Assert.AreEqual(0, result.Windows.Single(window => window.Title == "5h").Remaining);

        var none = OpenAiAccountUsageCollector.ParseUsage(new CodexAccountSnapshot("fake@example.com", "plus", Json("""
            {"rateLimits":{"primary":{"usedPercent":10},"credits":{"hasCredits":false,"unlimited":false,"balance":"0"}}}
            """)));
        Assert.HasCount(1, none.Windows);
        Assert.IsFalse(none.Windows.Any(window => window.IsBalance));

        var unlimited = OpenAiAccountUsageCollector.ParseUsage(new CodexAccountSnapshot("fake@example.com", "plus", Json("""
            {"rateLimits":{"primary":{"usedPercent":10},"credits":{"hasCredits":true,"unlimited":true,"balance":null}}}
            """)));
        Assert.AreEqual("Unlimited credits", unlimited.Windows.Single(window => window.IsBalance).RemainingText);

        var creditsOnly = OpenAiAccountUsageCollector.ParseUsage(new CodexAccountSnapshot("fake@example.com", "plus", Json("""
            {"rateLimits":{"primary":null,"credits":{"has_credits":true,"unlimited":false,"balance":"12.5"}}}
            """)));
        Assert.IsFalse(creditsOnly.IsUnavailable);
        Assert.AreEqual("12.5 credits", creditsOnly.Windows.Single().RemainingText);
    }

    [TestMethod]
    public void IdentityIncludesBothUserAndWorkspace()
    {
        using var context = new Context();
        var path = Path.Combine(context.Root, "identity");
        WriteAuth(path, "alice", "workspace");
        var original = OpenAiIdentityFiles.ReadIdentity(path);
        WriteAuth(path, "bob", "workspace");
        Assert.AreNotEqual(original, OpenAiIdentityFiles.ReadIdentity(path));
        WriteAuth(path, "alice", "different-workspace");
        Assert.AreNotEqual(original, OpenAiIdentityFiles.ReadIdentity(path));
    }

    [TestMethod]
    public async Task RpcHandlesEarlyLoginNotificationsAndNeverExposesServerErrorPayload()
    {
        using var output = new StringWriter();
        using var rpc = new CodexRpcConnection(new StringReader("""
            {"id":1,"result":{}}
            {"method":"account/login/completed","params":{"loginId":"fake-login","success":true}}
            {"id":2,"result":{"loginId":"fake-login"}}
            {"id":3,"error":{"code":401,"message":"SECRET_FAKE_TOKEN"}}
            """), output);
        await rpc.InitializeAsync(default);
        await rpc.RequestAsync("account/login/start", new { type = "chatgpt" }, default);
        Assert.IsTrue((await rpc.WaitForNotificationAsync("account/login/completed", "fake-login", default)).GetProperty("success").GetBoolean());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.RequestAsync("account/rateLimits/read", new { }, default));
        Assert.IsFalse(error.ToString().Contains("SECRET_FAKE_TOKEN"));
        StringAssert.Contains(output.ToString(), "initialized");
        Assert.IsFalse(output.ToString().Contains("thread/start"));
    }

    [TestMethod]
    public void ProcessLaunchUsesOnlyTheSavedHomeAndHiddenStandardStreams()
    {
        var originalHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var info = CodexAccountClient.BuildStartInfo(@"C:\tools\codex.exe", @"C:\saved accounts\personal");
        Assert.AreEqual(@"C:\saved accounts\personal", info.Environment["CODEX_HOME"]);
        Assert.AreEqual(originalHome, Environment.GetEnvironmentVariable("CODEX_HOME"));
        Assert.IsTrue(info.CreateNoWindow && info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
        Assert.IsFalse(info.UseShellExecute);
        Assert.IsFalse(info.Environment.ContainsKey("CODEX_API_KEY"));
        CollectionAssert.Contains(info.ArgumentList.ToArray(), "cli_auth_credentials_store=\"file\"");
    }

    [TestMethod]
    public async Task ExpiredAccessTokenRequestsCodexRenewalThenRetriesUsageOnce()
    {
        using var output = new StringWriter();
        var client = new CodexAccountClient(_ => new CodexRpcConnection(new StringReader("""
            {"id":1,"result":{}}
            {"id":2,"result":{"account":{"type":"chatgpt","email":"fake@example.com","planType":"plus"}}}
            {"id":3,"error":{"code":401,"message":"Unauthorized SECRET_FAKE_TOKEN"}}
            {"id":4,"result":{"account":{"type":"chatgpt","email":"fake@example.com","planType":"plus"}}}
            {"id":5,"result":{"rateLimits":{"primary":{"usedPercent":65}}}}
            """), output));
        var snapshot = await client.ReadAsync("fake home", default);
        Assert.AreEqual(35, OpenAiAccountUsageCollector.ParseUsage(snapshot).Windows.Single().Remaining);
        var requests = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Json).ToList();
        var renewals = requests.Where(request => CodexAccountClient.JsonString(request, "method") == "account/read" && request.GetProperty("params").GetProperty("refreshToken").GetBoolean());
        Assert.HasCount(1, renewals.ToList());
        Assert.IsFalse(output.ToString().Contains("SECRET_FAKE_TOKEN"));
    }

    [TestMethod]
    public async Task WindowsNpmShimTransportReadsRpcAndStopsItsChildProcess()
    {
        using var context = new Context();
        var directory = Path.Combine(context.Root, "fake codex home");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(context.Root, "fake-server.ps1");
        File.WriteAllText(script, """
            [IO.File]::WriteAllText((Join-Path $env:CODEX_HOME 'fake-server.pid'), [string]$PID)
            while ($null -ne ($line = [Console]::In.ReadLine())) {
                $request = $line | ConvertFrom-Json
                if ($null -ne $request.id) {
                    $reply = @{ id = $request.id; result = @{ observedHome = $env:CODEX_HOME } } | ConvertTo-Json -Compress
                    [Console]::Out.WriteLine($reply)
                    [Console]::Out.Flush()
                }
            }
            """);
        var shim = Path.Combine(context.Root, "fake-codex.cmd");
        File.WriteAllText(shim, "@echo off\r\npowershell.exe -NoLogo -NoProfile -NonInteractive -File \"%~dp0fake-server.ps1\" %*\r\n");
        var process = new Process { StartInfo = CodexAccountClient.BuildStartInfo(shim, directory) };
        process.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        int childId;
        using (var rpc = new CodexRpcConnection(process))
        {
            await rpc.InitializeAsync(timeout.Token);
            var response = await rpc.RequestAsync("account/read", new { refreshToken = false }, timeout.Token);
            Assert.AreEqual(directory, response.GetProperty("observedHome").GetString());
            childId = int.Parse(File.ReadAllText(Path.Combine(directory, "fake-server.pid")));
            // Cancellation of a blocked read still disposes the process tree.
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
            await Assert.ThrowsAsync<OperationCanceledException>(() => rpc.WaitForNotificationAsync("never", "fake", cancelled.Token));
        }
        try
        {
            using var child = Process.GetProcessById(childId);
            Assert.IsTrue(child.HasExited || child.WaitForExit(2000));
        }
        catch (ArgumentException) { /* Process already exited and was reaped. */ }
    }

    [DataTestMethod]
    [DataRow("Seth")]
    [DataRow("Seth - Personal & Work")]
    [DataRow("東京 / Seth's account")]
    public async Task GroupedCompactLabelsPreserveAccountAndCardIdentity(string label)
    {
        var account = new ProviderAccount { Id = "test", ProviderName = KnownProviders.Anthropic, Label = label };
        var collector = new AccountScopedUsageCollector(new GroupedCollector(), account.DisplayKey, KnownProviders.Anthropic, account.CardLabel);
        var vm = new UsageOverlayViewModel();
        vm.ApplyProvider(await collector.CollectAsync(default));
        var card = vm.Providers.Single();
        Assert.AreEqual($"Fable - {label} - 35%", card.MiniSummaryText);
        Assert.AreEqual(account.DisplayKey, card.SourceProviderName);
        vm.SetChecking([account.DisplayKey]);
        Assert.AreSame(card, vm.Providers.Single());
        Assert.AreEqual($"Fable - {label} - checking", card.MiniSummaryText);
        vm.ApplyProvider(await collector.CollectAsync(default));
        Assert.HasCount(1, vm.Providers);
        Assert.AreEqual($"Fable - {label} - 35%", vm.Providers.Single().MiniSummaryText);
    }

    [TestMethod]
    public void UnnamedDefaultsRemainShortAndUnavailableGroupsKeepAccountLabel()
    {
        var account = ProviderAccount.CreateDefaultAnthropic();
        Assert.AreEqual("", account.CardLabel);
        account.Label = "Seth";
        Assert.AreEqual("Seth", account.CardLabel);
        var card = new ProviderUsageCard(new ProviderUsage { Name = "Anthropic - Seth Fable", SourceProviderName = "Anthropic - Seth", AccountLabel = "Seth", IsUnavailable = true });
        Assert.AreEqual("Fable - Seth - unavailable", card.MiniSummaryText);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static CodexAccountSnapshot Snapshot(double used) => new("fake@example.com", "plus", Json(JsonSerializer.Serialize(new
    { rateLimits = new { primary = new { usedPercent = used, windowDurationMins = 300 } } })));
    private static void WriteAuth(string directory, string user, string workspace, string marker = "fake-access-token")
    {
        Directory.CreateDirectory(directory);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = user }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(Path.Combine(directory, "auth.json"), JsonSerializer.Serialize(new { tokens = new { account_id = workspace, id_token = "fake." + payload + ".fake", access_token = marker } }));
    }

    private sealed class FakeClient : ICodexAccountClient
    {
        public string User = "alice";
        public string TokenMarker = "fake-access-token";
        public List<string> ReadDirectories { get; } = [];
        public Func<string, CancellationToken, Task<CodexAccountSnapshot>> ReadHandler = (_, _) => Task.FromResult(Snapshot(25));
        public Func<string, CancellationToken, Task>? LoginHandler;
        public Task<CodexAccountSnapshot> ReadAsync(string directory, CancellationToken token) { ReadDirectories.Add(directory); return ReadHandler(directory, token); }
        public Task LoginAsync(string directory, Action<Uri> browser, CancellationToken token)
        { if (LoginHandler is not null) return LoginHandler(directory, token); WriteAuth(directory, User, "workspace", TokenMarker); return Task.CompletedTask; }
    }

    private sealed class Context : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        public AppSettingsService Settings { get; }
        public FakeClient Client { get; } = new();
        public OpenAiAccountManagerService Manager { get; }
        public Context() { Directory.CreateDirectory(Root); Settings = new(Root); Settings.Save(new AppSettings()); Manager = new(Settings, Client, Path.Combine(Root, "openai")); }
        public Task<ProviderAccount> Add(string label, string user, double used)
        { Client.User = user; Client.ReadHandler = (_, _) => Task.FromResult(Snapshot(used)); return Manager.LoginAsync(label, null, _ => { }, default); }
        public void Dispose() { Directory.Delete(Root, recursive: true); }
    }
    private sealed class DelayedCollector(int delay, double used) : IUsageCollector
    {
        public string ProviderName => KnownProviders.OpenAI;
        public async Task<ProviderUsage> CollectAsync(CancellationToken token) { await Task.Delay(delay, token); return OpenAiAccountUsageCollector.ParseUsage(Snapshot(used)); }
    }
    private sealed class GroupedCollector : IUsageCollector
    {
        public string ProviderName => KnownProviders.Anthropic;
        public Task<ProviderUsage> CollectAsync(CancellationToken token) => Task.FromResult(new ProviderUsage
        { Name = ProviderName, Windows = [new UsageWindow { Title = "Fable", DisplayGroupName = "Fable", Limit = 100, Used = 65, Remaining = 35 }] });
    }
}
