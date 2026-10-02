using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ClaudeBillingServiceTests
{
    private const string AccountId = "test-account";
    private const string OrgId = "ab9cc8d1-8d50-4f96-805d-7c22f52bcae2";
    private string _root = null!;
    private AppSettingsService _settings = null!;
    private ProviderAccount _account = null!;
    private BillingHandler _handler = null!;

    [TestInitialize]
    public void Initialize()
    {
        _handler = new BillingHandler();
        _root = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new AppSettingsService(_root);
        _account = new ProviderAccount
        {
            Id = "anthropic-test", ProviderName = KnownProviders.Anthropic, Label = "Personal",
            AccountUuid = AccountId, ConfigDir = Path.Combine(_root, "account")
        };
        Directory.CreateDirectory(_account.ConfigDir);
        File.WriteAllText(ClaudeBillingService.CredentialsPath(_account), """{"claudeAiOauth":{"accessToken":"test-token"}}""");
        _settings.Update(settings => settings.ProviderAccounts.Add(_account));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private ClaudeBillingService Service(HttpClient client) => new(_settings, client, Path.Combine(_root, "profiles"), _handler);
    private ProviderAccount Saved() => _settings.Load().ProviderAccounts.Single(a => a.Id == _account.Id);

    [TestMethod]
    public async Task ConnectVerifiesBothIdentitiesAndEncryptsSession()
    {
        using var client = new HttpClient(_handler);
        var connection = await Service(client).ConnectAsync(_account, "sessionKey=test-cookie", Guid.NewGuid().ToString("N"), CancellationToken.None, "test-browser");
        Assert.AreEqual(AccountId, connection.AccountUuid);
        Assert.AreEqual(OrgId, connection.OrganizationUuid);
        Assert.AreEqual("sessionKey=test-cookie", ProtectedStringService.Unprotect(connection.CookieHeaderProtected));
        Assert.IsFalse(File.ReadAllText(_settings.SettingsPath).Contains("test-cookie"));
        Assert.AreEqual("test-browser", Saved().ClaudeBilling!.UserAgent);
        var result = await Service(client).ReadAsync(Saved(), ClaudeBillingService.CredentialsPath(_account), CancellationToken.None);
        Assert.AreEqual("$113.85 credits", result.Balance!.RemainingText);
        Assert.AreEqual(2, _handler.Profiles.Count);
        Assert.IsTrue(_handler.Profiles.All(p => p == Service(client).GetProfileDirectory(connection.Id)));
        Assert.AreEqual(2, _handler.DisposedSessions);
        CollectionAssert.AreEqual(new[] { "/api/account", "/api/organizations", $"/api/organizations/{OrgId}/prepaid/credits",
            "/api/account", "/api/organizations", $"/api/organizations/{OrgId}/prepaid/credits" }, _handler.Paths);
    }

    [TestMethod]
    [DataRow("different-user", OrgId)]
    [DataRow(AccountId, "e4b70c1e-bf45-4c2d-ac64-526bac49f264")]
    public async Task WrongWebAccountOrOrganizationCannotBeLinked(string webAccount, string webOrg)
    {
        _handler.WebAccount = webAccount;
        _handler.WebOrg = webOrg;
        using var client = new HttpClient(_handler);
        await Assert.ThrowsExactlyAsync<ClaudeBillingException>(() => Service(client).ConnectAsync(
            _account, "sessionKey=test-cookie", Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.IsNull(Saved().ClaudeBilling);
    }

    [TestMethod]
    public async Task WrongOAuthIdentityCannotBeLinkedEvenWhenLabelAndWebAccountMatch()
    {
        _handler.OAuthAccount = "other-login";
        using var client = new HttpClient(_handler);
        await Assert.ThrowsExactlyAsync<ClaudeBillingException>(() => Service(client).ConnectAsync(
            _account, "sessionKey=test-cookie", Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.IsNull(Saved().ClaudeBilling);
    }

    [TestMethod]
    [DataRow(11385, "USD", "$113.85 credits")]
    [DataRow(0, "USD", "$0.00 credits")]
    [DataRow(-27, "USD", "$-0.27 credits")]
    [DataRow(11385, "JPY", "11385 JPY credits")]
    [DataRow(11385, "KWD", "11.385 KWD credits")]
    public void BalanceUsesCurrencyAndPreservesReportedZeroAndDebt(int amount, string currency, string expected)
    {
        using var json = JsonSerializer.SerializeToDocument(new { amount, currency });
        var balance = ClaudeBillingService.ParseBalance(json.RootElement);
        Assert.AreEqual(expected, balance.RemainingText);
        Assert.IsTrue(balance.IsBalance);
        Assert.AreEqual(string.Empty, balance.DisplayGroupName);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"amount\":null,\"currency\":\"USD\"}")]
    [DataRow("{\"amount\":100,\"currency\":\"bad\"}")]
    [DataRow("{\"amount\":100,\"currency\":\"USD\",\"decimal_places\":null}")]
    [DataRow("{\"amount\":1e100,\"currency\":\"USD\"}")]
    public void MissingOrMalformedBalancesAreNotZero(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.ThrowsExactly<ClaudeBillingException>(() => ClaudeBillingService.ParseBalance(json.RootElement));
    }

    [TestMethod]
    [DataRow(0, "", ClaudeBillingConnectionState.ReconnectRequired)]
    [DataRow(302, "", ClaudeBillingConnectionState.ReconnectRequired)]
    [DataRow(401, "", ClaudeBillingConnectionState.ReconnectRequired)]
    [DataRow(403, "forbidden", ClaudeBillingConnectionState.ReconnectRequired)]
    [DataRow(403, "cloudflare challenge", ClaudeBillingConnectionState.TemporarilyUnavailable)]
    [DataRow(429, "", ClaudeBillingConnectionState.TemporarilyUnavailable)]
    [DataRow(500, "", ClaudeBillingConnectionState.TemporarilyUnavailable)]
    [DataRow(200, "<html>challenge</html>", ClaudeBillingConnectionState.TemporarilyUnavailable)]
    public async Task BillingFailuresNeverReturnAPlausibleBalance(int status, string body, ClaudeBillingConnectionState expected)
    {
        var handler = _handler;
        using var client = new HttpClient(handler);
        await Service(client).ConnectAsync(_account, "sessionKey=test-cookie", Guid.NewGuid().ToString("N"), CancellationToken.None);
        handler.Failure = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        var result = await Service(client).ReadAsync(Saved(), ClaudeBillingService.CredentialsPath(_account), CancellationToken.None);
        Assert.IsNull(result.Balance);
        Assert.AreEqual(expected, result.State);
        Assert.AreEqual(2, _handler.DisposedSessions, "A failed browser read must release its profile.");
    }

    [TestMethod]
    public async Task TimeoutIsTemporaryButUserCancellationPropagates()
    {
        var handler = _handler;
        using var client = new HttpClient(handler);
        await Service(client).ConnectAsync(_account, "sessionKey=test-cookie", Guid.NewGuid().ToString("N"), CancellationToken.None);
        handler.Timeout = true;
        var result = await Service(client).ReadAsync(Saved(), ClaudeBillingService.CredentialsPath(_account), CancellationToken.None);
        Assert.AreEqual(ClaudeBillingConnectionState.TemporarilyUnavailable, result.State);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(client).ReadAsync(Saved(), ClaudeBillingService.CredentialsPath(_account), cancel.Token));
    }

    [TestMethod]
    public async Task DisconnectAndAccountRemovalEraseCredentialsAndIsolatedProfile()
    {
        using var client = new HttpClient(_handler);
        var service = Service(client);
        var connection = await service.ConnectAsync(_account, "sessionKey=test-cookie", Guid.NewGuid().ToString("N"), CancellationToken.None);
        Directory.CreateDirectory(service.GetProfileDirectory(connection.Id));
        File.WriteAllText(Path.Combine(service.GetProfileDirectory(connection.Id), "dummy-cookie"), "fake");
        await service.DisconnectAsync(_account.Id);
        Assert.IsNull(Saved().ClaudeBilling);
        Assert.IsFalse(Directory.Exists(service.GetProfileDirectory(connection.Id)));
        connection = await service.ConnectAsync(Saved(), "sessionKey=test-cookie", Guid.NewGuid().ToString("N"), CancellationToken.None);
        Directory.CreateDirectory(service.GetProfileDirectory(connection.Id));
        var manager = new AnthropicAccountManagerService(_settings, new AppLogService(_root), accountsRootDirectory: _root, billingService: service);
        Assert.IsTrue(manager.RemoveAccount(_account.Id));
        Assert.IsFalse(Directory.Exists(service.GetProfileDirectory(connection.Id)));
        Assert.IsFalse(_settings.Load().ProviderAccounts.Any(a => a.Id == _account.Id));
    }

    [TestMethod]
    public async Task CachedSettingsAndAccountRenamePreserveNewConnection()
    {
        var cached = _settings.Load();
        using var client = new HttpClient(_handler);
        var connection = await Service(client).ConnectAsync(_account, "sessionKey=test-cookie", Guid.NewGuid().ToString("N"), CancellationToken.None);
        var manager = new AnthropicAccountManagerService(_settings, new AppLogService(_root));
        await Task.WhenAll(Task.Run(() => manager.RenameAccount(_account.Id, "Renamed")),
            Task.Run(() => _settings.Update(s => s.ProviderAccounts.Add(new ProviderAccount { Id = "openai-test", ProviderName = KnownProviders.OpenAI, Label = "Other" }))));
        _settings.SavePreservingAccounts(cached);
        Assert.AreEqual(connection.Id, Saved().ClaudeBilling!.Id);
        Assert.AreEqual("Renamed", Saved().Label);
        Assert.IsTrue(_settings.Load().ProviderAccounts.Any(a => a.Id == "openai-test"));
        var clone = Saved().Clone();
        clone.ClaudeBilling!.Message = "changed clone";
        Assert.AreNotEqual(clone.ClaudeBilling.Message, Saved().ClaudeBilling!.Message);
    }

    [TestMethod]
    public async Task ConnectBorrowsVisibleBrowserAndDoesNotOpenAnotherProfile()
    {
        using var client = new HttpClient(_handler);
        await using var visible = new BillingHandler.FakeSession(_handler);
        await Service(client).ConnectAsync(_account, "sessionKey=test-cookie", Guid.NewGuid().ToString("N"),
            CancellationToken.None, browserSession: visible);
        Assert.AreEqual(0, _handler.Profiles.Count);
        Assert.AreEqual(0, _handler.DisposedSessions, "Setup owns the visible browser's lifetime.");
        Assert.AreEqual("Connected: $113.85 credits", Saved().ClaudeBilling!.Message);
    }

    [TestMethod]
    [DataRow("another-user", OrgId)]
    [DataRow(AccountId, "e4b70c1e-bf45-4c2d-ac64-526bac49f264")]
    public async Task ChangedWebsiteIdentityDuringRefreshCannotPublishBalance(string accountId, string orgId)
    {
        using var client = new HttpClient(_handler);
        var service = Service(client);
        await service.ConnectAsync(_account, "sessionKey=test-cookie", Guid.NewGuid().ToString("N"), CancellationToken.None);
        _handler.WebAccount = accountId;
        _handler.WebOrg = orgId;
        var result = await service.ReadAsync(Saved(), ClaudeBillingService.CredentialsPath(_account), CancellationToken.None);
        Assert.IsNull(result.Balance);
        Assert.AreEqual(ClaudeBillingConnectionState.ReconnectRequired, result.State);
        Assert.AreEqual(1, _handler.Paths.Count(p => p.EndsWith("/prepaid/credits", StringComparison.Ordinal)),
            "The changed browser login must be rejected before requesting another balance.");
    }

    private sealed class BillingHandler : HttpMessageHandler, IClaudeBillingBrowser
    {
        public string WebAccount { get; set; } = AccountId;
        public string WebOrg { get; set; } = OrgId;
        public string OAuthAccount { get; set; } = AccountId;
        public HttpResponseMessage? Failure { get; set; }
        public bool Timeout { get; set; }
        public List<string> Profiles { get; } = [];
        public List<string> Paths { get; } = [];
        public int DisposedSessions { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("api.anthropic.com", request.RequestUri!.Host, "Website requests must use the browser, never HttpClient.");
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.AreEqual("test-token", request.Headers.Authorization!.Parameter);
            var body = new { account = new { uuid = OAuthAccount }, organization = new { uuid = OrgId } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }

        public Task<IClaudeBillingWebSession> OpenAsync(string profileDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Profiles.Add(profileDirectory);
            return Task.FromResult<IClaudeBillingWebSession>(new FakeSession(this));
        }

        public sealed class FakeSession(BillingHandler handler) : IClaudeBillingWebSession
        {
            public async Task<ClaudeBillingWebResponse> GetAsync(string path, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                handler.Paths.Add(path);
                if (handler.Timeout) throw new TaskCanceledException("fake browser timeout");
                if (handler.Failure is { } failure)
                {
                    handler.Failure = null;
                    using (failure) return new((int)failure.StatusCode, await failure.Content.ReadAsStringAsync(cancellationToken));
                }
                object body = path switch
                {
                    "/api/account" => new { uuid = handler.WebAccount },
                    "/api/organizations" => new[] { new { uuid = handler.WebOrg } },
                    _ when path == $"/api/organizations/{OrgId}/prepaid/credits" => new { amount = 11385, currency = "USD" },
                    _ => throw new AssertFailedException("Unexpected billing endpoint")
                };
                return new(200, JsonSerializer.Serialize(body));
            }
            public ValueTask DisposeAsync()
            {
                handler.DisposedSessions++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
