using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ClaudeAccountSwitchServiceTests
{
    private string _tempRoot = string.Empty;
    private string _homeDirectory = string.Empty;
    private string _claudeDirectory = string.Empty;
    private string _accountDirectory = string.Empty;
    private AppSettingsService _settingsService = null!;
    private AppLogService _logService = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        _homeDirectory = Path.Combine(_tempRoot, "home");
        _claudeDirectory = Path.Combine(_homeDirectory, ".claude");
        _accountDirectory = Path.Combine(_tempRoot, "accounts", "work");
        Directory.CreateDirectory(_claudeDirectory);
        Directory.CreateDirectory(_accountDirectory);
        _settingsService = new AppSettingsService(_tempRoot);
        _logService = new AppLogService(_tempRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public async Task SwitchCopiesTargetCredentialsAndBacksUpAndClearsCaches()
    {
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "default-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        File.WriteAllText(Path.Combine(_claudeDirectory, "ai-usage-monitor-profile.json"), "{}");
        File.WriteAllText(Path.Combine(_claudeDirectory, "ai-usage-monitor-oauth-usage-cache.json"), "{}");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(profileUuid: "uuid-default");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        var swapped = File.ReadAllText(Path.Combine(_claudeDirectory, ".credentials.json"));
        StringAssert.Contains(swapped, "work-token");
        Assert.AreEqual(1, Directory.GetFiles(_claudeDirectory, ".credentials.json.aium-backup-*").Length);
        Assert.IsFalse(File.Exists(Path.Combine(_claudeDirectory, "ai-usage-monitor-profile.json")));
        Assert.IsFalse(File.Exists(Path.Combine(_claudeDirectory, "ai-usage-monitor-oauth-usage-cache.json")));

        var savedSettings = _settingsService.Load();
        var defaultAccount = savedSettings.ProviderAccounts.Single(account => account.IsDefault);
        Assert.AreEqual("uuid-work", defaultAccount.AccountUuid);
    }

    [TestMethod]
    public async Task SwitchSyncsCurrentCredentialsBackToMatchingManagedAccount()
    {
        var otherAccountDirectory = Path.Combine(_tempRoot, "accounts", "personal");
        Directory.CreateDirectory(otherAccountDirectory);
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "personal-rotated-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        WriteCredentials(Path.Combine(otherAccountDirectory, ".credentials.json"), "personal-old-token");
        var target = SaveAccounts(
            targetUuid: "uuid-work",
            extraAccount: new ProviderAccount
            {
                Id = "anthropic-personal",
                ProviderName = KnownProviders.Anthropic,
                Label = "Personal",
                ConfigDir = otherAccountDirectory,
                AccountUuid = "uuid-personal"
            });
        var service = CreateService(profileUuid: "uuid-personal");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        var syncedBack = File.ReadAllText(Path.Combine(otherAccountDirectory, ".credentials.json"));
        StringAssert.Contains(syncedBack, "personal-rotated-token");
    }

    [TestMethod]
    public async Task SwitchToAlreadyActiveAccountIsNoOp()
    {
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "work-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(profileUuid: "uuid-work");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.AlreadyActive);
        Assert.AreEqual(0, Directory.GetFiles(_claudeDirectory, ".credentials.json.aium-backup-*").Length);
    }

    [TestMethod]
    public async Task SwitchAdoptsUnknownOutgoingIdentityForSwitchBack()
    {
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "default-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(profileUuid: "uuid-personal");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        var savedSettings = _settingsService.Load();
        var adopted = savedSettings.ProviderAccounts.SingleOrDefault(account =>
            !account.IsDefault && account.AccountUuid == "uuid-personal");
        Assert.IsNotNull(adopted, "the outgoing login must be kept as its own account");
        Assert.AreEqual("uuid-personal@example.com", adopted!.Email);
        var adoptedCredentials = File.ReadAllText(Path.Combine(adopted.ConfigDir, ".credentials.json"));
        StringAssert.Contains(adoptedCredentials, "default-token");
    }

    [TestMethod]
    public async Task SwitchResetsDefaultLabelAndTakesTargetIdentity()
    {
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "default-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var settings = _settingsService.Load();
        settings.ProviderAccounts.Single(account => account.IsDefault).Label = "Personal";
        _settingsService.Save(settings);
        var service = CreateService(profileUuid: "uuid-personal");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        var defaultAccount = _settingsService.Load().ProviderAccounts.Single(account => account.IsDefault);
        Assert.AreEqual(ProviderAccount.DefaultAccountLabel, defaultAccount.Label);
        Assert.AreEqual("uuid-work", defaultAccount.AccountUuid);
        var adopted = _settingsService.Load().ProviderAccounts.Single(account =>
            !account.IsDefault && account.AccountUuid == "uuid-personal");
        Assert.AreEqual("Personal", adopted.Label, "the outgoing login inherits the default row's custom name");
    }

    [TestMethod]
    public async Task SwitchSwapsOAuthAccountIdentityBlockAndPreservesOtherHomeSettings()
    {
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "default-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        WriteClaudeJson(Path.Combine(_homeDirectory, ".claude.json"), "uuid-personal", "personal@example.com",
            extra: new JsonObject { ["numStartups"] = 42, ["projects"] = new JsonObject { ["D:\\proj"] = new JsonObject() } });
        WriteClaudeJson(Path.Combine(_accountDirectory, ".claude.json"), "uuid-work", "work@example.com");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(profileUuid: "uuid-personal");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        var homeRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(_homeDirectory, ".claude.json")))!.AsObject();
        Assert.AreEqual("uuid-work", homeRoot["oauthAccount"]?["accountUuid"]?.GetValue<string>(),
            "the /status identity block must follow the token");
        Assert.AreEqual(42, homeRoot["numStartups"]?.GetValue<int>(), "unrelated home settings must survive the swap");
        Assert.IsNotNull(homeRoot["projects"], "unrelated home settings must survive the swap");
        Assert.AreEqual(1, Directory.GetFiles(_claudeDirectory, ".claude.json.aium-backup-*").Length,
            "the identity file must be backed up before the swap");

        // The outgoing identity was adopted; its dir must carry the old identity block too.
        var adopted = _settingsService.Load().ProviderAccounts.Single(account =>
            !account.IsDefault && account.AccountUuid == "uuid-personal");
        var adoptedRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(adopted.ConfigDir, ".claude.json")))!.AsObject();
        Assert.AreEqual("uuid-personal", adoptedRoot["oauthAccount"]?["accountUuid"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task SwitchSyncsOAuthAccountBlockBackToMatchingManagedAccount()
    {
        var otherAccountDirectory = Path.Combine(_tempRoot, "accounts", "personal");
        Directory.CreateDirectory(otherAccountDirectory);
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "personal-rotated-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        WriteCredentials(Path.Combine(otherAccountDirectory, ".credentials.json"), "personal-old-token");
        WriteClaudeJson(Path.Combine(_homeDirectory, ".claude.json"), "uuid-personal", "fresh-personal@example.com");
        WriteClaudeJson(Path.Combine(otherAccountDirectory, ".claude.json"), "uuid-personal", "stale-personal@example.com");
        var target = SaveAccounts(
            targetUuid: "uuid-work",
            extraAccount: new ProviderAccount
            {
                Id = "anthropic-personal",
                ProviderName = KnownProviders.Anthropic,
                Label = "Personal",
                ConfigDir = otherAccountDirectory,
                AccountUuid = "uuid-personal"
            });
        var service = CreateService(profileUuid: "uuid-personal");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        var syncedRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(otherAccountDirectory, ".claude.json")))!.AsObject();
        Assert.AreEqual("fresh-personal@example.com", syncedRoot["oauthAccount"]?["emailAddress"]?.GetValue<string>(),
            "the managed dir must receive the freshest identity block from home");
    }

    [TestMethod]
    public async Task SwitchDoesNotSyncStaleHomeIdentityBlockIntoManagedDir()
    {
        var otherAccountDirectory = Path.Combine(_tempRoot, "accounts", "personal");
        Directory.CreateDirectory(otherAccountDirectory);
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "personal-rotated-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        WriteCredentials(Path.Combine(otherAccountDirectory, ".credentials.json"), "personal-old-token");
        // Home block is a split-brain leftover: it does NOT match the active identity.
        WriteClaudeJson(Path.Combine(_homeDirectory, ".claude.json"), "uuid-stale", "stale@example.com");
        WriteClaudeJson(Path.Combine(otherAccountDirectory, ".claude.json"), "uuid-personal", "correct-personal@example.com");
        var target = SaveAccounts(
            targetUuid: "uuid-work",
            extraAccount: new ProviderAccount
            {
                Id = "anthropic-personal",
                ProviderName = KnownProviders.Anthropic,
                Label = "Personal",
                ConfigDir = otherAccountDirectory,
                AccountUuid = "uuid-personal"
            });
        var service = CreateService(profileUuid: "uuid-personal");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        var personalRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(otherAccountDirectory, ".claude.json")))!.AsObject();
        Assert.AreEqual("correct-personal@example.com", personalRoot["oauthAccount"]?["emailAddress"]?.GetValue<string>(),
            "a mismatched home identity block must never overwrite the managed dir's correct block");
    }

    [TestMethod]
    public async Task SwitchRebuildsStaleTargetIdentityBlockFromProfileEndpoint()
    {
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "default-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        WriteClaudeJson(Path.Combine(_homeDirectory, ".claude.json"), "uuid-stale", "stale@example.com");
        // The target dir's cached block belongs to a different account (split-brain leftover).
        WriteClaudeJson(Path.Combine(_accountDirectory, ".claude.json"), "uuid-stale", "stale@example.com");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(
            profileUuid: "uuid-personal",
            tokenUuids: new Dictionary<string, string>
            {
                ["default-token"] = "uuid-personal",
                ["work-token"] = "uuid-work"
            });

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        var homeRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(_homeDirectory, ".claude.json")))!.AsObject();
        Assert.AreEqual("uuid-work", homeRoot["oauthAccount"]?["accountUuid"]?.GetValue<string>(),
            "the home identity block must be rebuilt from the profile endpoint when the target's cached block is stale");
        Assert.AreEqual("uuid-work@example.com", homeRoot["oauthAccount"]?["emailAddress"]?.GetValue<string>());
        var targetRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(_accountDirectory, ".claude.json")))!.AsObject();
        Assert.AreEqual("uuid-work", targetRoot["oauthAccount"]?["accountUuid"]?.GetValue<string>(),
            "the target dir's stale block must be healed too");
    }

    [TestMethod]
    public async Task AlreadyActiveSwitchRepairsStaleHomeIdentityBlock()
    {
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "work-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        // Split-brain: token is the work account, cached identity claims someone else.
        WriteClaudeJson(Path.Combine(_homeDirectory, ".claude.json"), "uuid-stale", "stale@example.com");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(
            profileUuid: "uuid-work",
            tokenUuids: new Dictionary<string, string> { ["work-token"] = "uuid-work" });

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.AlreadyActive);
        var homeRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(_homeDirectory, ".claude.json")))!.AsObject();
        Assert.AreEqual("uuid-work", homeRoot["oauthAccount"]?["accountUuid"]?.GetValue<string>(),
            "an already-active switch must still repair a stale /status identity");
    }

    [TestMethod]
    public async Task RepairIdentityCacheFixesStaleHomeBlockWithoutSwitching()
    {
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "work-token");
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        WriteClaudeJson(Path.Combine(_homeDirectory, ".claude.json"), "uuid-stale", "stale@example.com");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(
            profileUuid: "uuid-work",
            tokenUuids: new Dictionary<string, string> { ["work-token"] = "uuid-work" });

        var repaired = await service.RepairIdentityCacheAsync(target, CancellationToken.None);

        Assert.IsTrue(repaired);
        var homeRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(_homeDirectory, ".claude.json")))!.AsObject();
        Assert.AreEqual("uuid-work", homeRoot["oauthAccount"]?["accountUuid"]?.GetValue<string>());
        StringAssert.Contains(File.ReadAllText(Path.Combine(_claudeDirectory, ".credentials.json")), "work-token",
            "repair must never touch the token itself");

        var repairedAgain = await service.RepairIdentityCacheAsync(target, CancellationToken.None);
        Assert.IsFalse(repairedAgain, "a consistent state must be a no-op");
    }

    [TestMethod]
    public async Task SwitchWithoutTargetLoginFails()
    {
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(profileUuid: "uuid-default");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "no saved login");
    }

    [TestMethod]
    public async Task SwitchWithMissingDefaultCredentialsCopiesWithoutBackup()
    {
        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(profileUuid: "uuid-default");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        StringAssert.Contains(File.ReadAllText(Path.Combine(_claudeDirectory, ".credentials.json")), "work-token");
        Assert.AreEqual(0, Directory.GetFiles(_claudeDirectory, ".credentials.json.aium-backup-*").Length);
    }

    [TestMethod]
    public async Task BackupsArePrunedToNewestThree()
    {
        WriteCredentials(Path.Combine(_claudeDirectory, ".credentials.json"), "default-token");
        for (var index = 0; index < 4; index++)
        {
            File.WriteAllText(
                Path.Combine(_claudeDirectory, $".credentials.json.aium-backup-2026010{index + 1}-000000"),
                "{}");
        }

        WriteCredentials(Path.Combine(_accountDirectory, ".credentials.json"), "work-token");
        var target = SaveAccounts(targetUuid: "uuid-work");
        var service = CreateService(profileUuid: "uuid-default");

        var result = await service.SwitchToAsync(target, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(3, Directory.GetFiles(_claudeDirectory, ".credentials.json.aium-backup-*").Length);
    }

    private ProviderAccount SaveAccounts(string targetUuid, ProviderAccount? extraAccount = null)
    {
        var target = new ProviderAccount
        {
            Id = "anthropic-work",
            ProviderName = KnownProviders.Anthropic,
            Label = "Work",
            ConfigDir = _accountDirectory,
            AccountUuid = targetUuid
        };

        var settings = new AppSettings();
        settings.ProviderAccounts.Add(target);
        if (extraAccount is not null)
        {
            settings.ProviderAccounts.Add(extraAccount);
        }

        _settingsService.Save(settings);
        return target;
    }

    private ClaudeAccountSwitchService CreateService(string profileUuid, IDictionary<string, string>? tokenUuids = null)
    {
        var handler = new ScriptedHttpHandler(request =>
        {
            var uri = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (uri.Contains("/oauth/profile", StringComparison.OrdinalIgnoreCase))
            {
                var token = request.Headers.Authorization?.Parameter ?? string.Empty;
                var uuid = tokenUuids is not null && tokenUuids.TryGetValue(token, out var mappedUuid)
                    ? mappedUuid
                    : profileUuid;
                var profile = new JsonObject
                {
                    ["account"] = new JsonObject
                    {
                        ["email"] = $"{uuid}@example.com",
                        ["uuid"] = uuid
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(profile.ToJsonString(), Encoding.UTF8, "application/json")
                };
            }

            // Token endpoint: never called in these tests because stored tokens are unexpired.
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var httpClient = new HttpClient(handler);
        var accountManager = new AnthropicAccountManagerService(
            _settingsService,
            _logService,
            httpClient,
            Path.Combine(_tempRoot, "accounts-root"));
        var tokenRefresher = new AnthropicOAuthTokenRefresher(httpClient, _logService);
        return new ClaudeAccountSwitchService(
            _settingsService,
            _logService,
            accountManager,
            tokenRefresher,
            _homeDirectory);
    }

    private static void WriteClaudeJson(string path, string accountUuid, string email, JsonObject? extra = null)
    {
        var root = extra ?? [];
        root["oauthAccount"] = new JsonObject
        {
            ["accountUuid"] = accountUuid,
            ["emailAddress"] = email,
            ["organizationName"] = email + "'s Org"
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString());
    }

    private static void WriteCredentials(string path, string accessToken)
    {
        var credentials = new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = accessToken,
                ["refreshToken"] = "refresh-" + accessToken,
                ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeMilliseconds()
            }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, credentials.ToJsonString());
    }

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
