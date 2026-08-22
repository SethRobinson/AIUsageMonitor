using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ClaudeSlotIdentityServiceTests
{
    private string _tempRoot = string.Empty;
    private string _homeDirectory = string.Empty;
    private AppLogService _logService = null!;
    private AppSettingsService _settingsService = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        _homeDirectory = Path.Combine(_tempRoot, "home");
        Directory.CreateDirectory(_homeDirectory);
        _logService = new AppLogService(_tempRoot);
        _settingsService = new AppSettingsService(_tempRoot);
        _settingsService.Save(new AppSettings());
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [TestMethod]
    public void NoCredentialsMeansNoSlotIdentity()
    {
        WriteClaudeJson(HomeClaudeJsonPath, "uuid-work", "work@example.com");
        var service = CreateService();

        Assert.IsFalse(service.HasLogin);
        Assert.IsNull(service.GetIdentity(), "a leftover identity block without a token is not a login");
    }

    [TestMethod]
    public void IdentityComesFromTheBlockTheCliCachesNextToTheToken()
    {
        WriteCredentials(HomeCredentialsPath, "token-personal");
        WriteClaudeJson(HomeClaudeJsonPath, "uuid-personal", "personal@example.com");
        var service = CreateService();

        var identity = service.GetIdentity();

        Assert.IsNotNull(identity);
        Assert.AreEqual("uuid-personal", identity.Uuid);
        Assert.AreEqual("personal@example.com", identity.Email);
        Assert.IsFalse(identity.IsVerified, "a cached block is a hint, not proof of who the token belongs to");
    }

    [TestMethod]
    public async Task ProfileEndpointOverridesASplitBrainBlock()
    {
        WriteCredentials(HomeCredentialsPath, "token-personal");
        // The block still names the previous account while the token belongs to another.
        WriteClaudeJson(HomeClaudeJsonPath, "uuid-work", "work@example.com");
        var service = CreateService(("token-personal", "uuid-personal"));

        var identity = await service.ResolveAsync(CancellationToken.None);

        Assert.IsNotNull(identity);
        Assert.AreEqual("uuid-personal", identity.Uuid, "the token is what authenticates and gets billed");
        Assert.IsTrue(identity.IsVerified);
        Assert.AreEqual("uuid-personal", service.GetIdentity()?.Uuid, "the verified answer is reused for the same token");
    }

    [TestMethod]
    public async Task ANewTokenIsVerifiedAgainInsteadOfReusingTheOldAnswer()
    {
        WriteCredentials(HomeCredentialsPath, "token-work");
        WriteClaudeJson(HomeClaudeJsonPath, "uuid-work", "work@example.com");
        var service = CreateService(("token-work", "uuid-work"), ("token-personal", "uuid-personal"));

        Assert.AreEqual("uuid-work", (await service.ResolveAsync(CancellationToken.None))?.Uuid);

        // The user logs ~/.claude into another account from outside the app.
        WriteCredentials(HomeCredentialsPath, "token-personal");
        WriteClaudeJson(HomeClaudeJsonPath, "uuid-personal", "personal@example.com");

        Assert.AreEqual("uuid-personal", service.GetIdentity()?.Uuid, "a changed token invalidates the cached answer");
        Assert.AreEqual("uuid-personal", (await service.ResolveAsync(CancellationToken.None))?.Uuid);
    }

    [TestMethod]
    public async Task OfflineVerificationFallsBackToTheCachedBlock()
    {
        WriteCredentials(HomeCredentialsPath, "token-personal");
        WriteClaudeJson(HomeClaudeJsonPath, "uuid-personal", "personal@example.com");
        var service = CreateService();

        var identity = await service.ResolveAsync(CancellationToken.None);

        Assert.IsNotNull(identity);
        Assert.AreEqual("uuid-personal", identity.Uuid);
        Assert.IsFalse(identity.IsVerified);
    }

    [TestMethod]
    public async Task LiveSlotTokensAreMirroredIntoTheMatchingAccountDir()
    {
        var account = CreateManagedAccount("uuid-work");
        WriteCredentials(Path.Combine(account.ConfigDir, ".credentials.json"), "token-work-old");
        WriteClaudeJson(Path.Combine(account.ConfigDir, ".claude.json"), "uuid-work", "work@example.com");
        File.SetLastWriteTimeUtc(
            Path.Combine(account.ConfigDir, ".credentials.json"),
            DateTime.UtcNow.AddDays(-3));

        WriteCredentials(HomeCredentialsPath, "token-work-rotated");
        WriteClaudeJson(HomeClaudeJsonPath, "uuid-work", "work@example.com");

        var service = CreateService(("token-work-rotated", "uuid-work"));
        await service.ResolveAsync(CancellationToken.None);

        Assert.IsTrue(service.TryMirrorSlotCredentials(account));
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(account.ConfigDir, ".credentials.json")),
            "token-work-rotated");
        Assert.IsFalse(service.TryMirrorSlotCredentials(account), "an already-current copy is not rewritten");
    }

    [TestMethod]
    public async Task MirroringNeverFilesOneAccountsTokensUnderAnother()
    {
        var account = CreateManagedAccount("uuid-work");
        WriteCredentials(Path.Combine(account.ConfigDir, ".credentials.json"), "token-work-old");
        File.SetLastWriteTimeUtc(
            Path.Combine(account.ConfigDir, ".credentials.json"),
            DateTime.UtcNow.AddDays(-3));

        // The slot holds Personal, but its cached block still claims Work.
        WriteCredentials(HomeCredentialsPath, "token-personal");
        WriteClaudeJson(HomeClaudeJsonPath, "uuid-work", "work@example.com");

        var service = CreateService(("token-personal", "uuid-personal"));
        await service.ResolveAsync(CancellationToken.None);

        Assert.IsFalse(service.TryMirrorSlotCredentials(account));
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(account.ConfigDir, ".credentials.json")),
            "token-work-old");
    }

    [TestMethod]
    public void UnverifiedIdentityIsNeverMirrored()
    {
        var account = CreateManagedAccount("uuid-work");
        WriteCredentials(Path.Combine(account.ConfigDir, ".credentials.json"), "token-work-old");
        File.SetLastWriteTimeUtc(
            Path.Combine(account.ConfigDir, ".credentials.json"),
            DateTime.UtcNow.AddDays(-3));

        WriteCredentials(HomeCredentialsPath, "token-work-rotated");
        WriteClaudeJson(HomeClaudeJsonPath, "uuid-work", "work@example.com");

        var service = CreateService();

        Assert.IsFalse(
            service.TryMirrorSlotCredentials(account),
            "without a token-verified identity the copy could belong to a different account");
    }

    private string HomeCredentialsPath => Path.Combine(_homeDirectory, ".claude", ".credentials.json");

    private string HomeClaudeJsonPath => Path.Combine(_homeDirectory, ".claude.json");

    private ProviderAccount CreateManagedAccount(string accountUuid)
    {
        var configDir = Path.Combine(_tempRoot, "accounts", accountUuid);
        Directory.CreateDirectory(configDir);
        return new ProviderAccount
        {
            Id = $"anthropic-{accountUuid}",
            ProviderName = KnownProviders.Anthropic,
            Label = "Work",
            Enabled = true,
            ConfigDir = configDir,
            AccountUuid = accountUuid
        };
    }

    // With no token-to-uuid mappings the profile endpoint always fails, which is what an
    // offline tick looks like.
    private ClaudeSlotIdentityService CreateService(params (string Token, string Uuid)[] profiles)
    {
        if (profiles.Length == 0)
        {
            return new ClaudeSlotIdentityService(_logService, accountManager: null, _homeDirectory);
        }

        var handler = new ScriptedHttpHandler(request =>
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            var match = profiles.FirstOrDefault(profile => profile.Token == token);
            if (match.Uuid is null)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var profile = new JsonObject
            {
                ["account"] = new JsonObject
                {
                    ["email"] = $"{match.Uuid}@example.com",
                    ["uuid"] = match.Uuid
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(profile.ToJsonString(), Encoding.UTF8, "application/json")
            };
        });

        var accountManager = new AnthropicAccountManagerService(
            _settingsService,
            _logService,
            new HttpClient(handler),
            Path.Combine(_tempRoot, "accounts-root"));
        return new ClaudeSlotIdentityService(_logService, accountManager, _homeDirectory);
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

    private static void WriteClaudeJson(string path, string accountUuid, string email)
    {
        var root = new JsonObject
        {
            ["oauthAccount"] = new JsonObject
            {
                ["accountUuid"] = accountUuid,
                ["emailAddress"] = email
            }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString());
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
