using System.Net;
using System.Net.Http;
using System.IO;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.Views;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class AnthropicApiCreditsCollectorTests
{
    private static readonly DateTimeOffset VerifiedAt = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ParseSnapshotReadsBalancePendingAndExpiry()
    {
        var snapshot = AnthropicApiCreditsCollector.ParseSnapshot(
            """{"amount":17000,"pending_invoice_amount_cents":1234}""",
            """{"remaining_amount_cents":5000,"expires_at":"2026-08-01T00:00:00Z"}""",
            Guid.NewGuid().ToString(),
            "Seth",
            VerifiedAt);

        Assert.AreEqual(17000m, snapshot.AmountCents);
        Assert.AreEqual(1234m, snapshot.PendingInvoiceAmountCents);
        Assert.AreEqual(5000m, snapshot.ExpiringAmountCents);
        Assert.AreEqual(DateTimeOffset.Parse("2026-08-01T00:00:00Z"), snapshot.ExpiresAt);

        var provider = AnthropicApiCreditsCollector.CreateProviderUsage(snapshot);
        Assert.AreEqual(KnownProviders.AnthropicApiCredits, provider.Name);
        Assert.AreEqual("Anthropic Console billing", provider.Source);
        var window = provider.Windows.Single();
        Assert.IsTrue(window.IsBalance);
        Assert.AreEqual("$170.00 left", window.RemainingText);
        StringAssert.Contains(window.Detail, "$12.34 pending this period");
        StringAssert.Contains(window.Detail, "$50.00 expires");
    }

    [TestMethod]
    public void ParseSnapshotAcceptsNumericStrings()
    {
        var snapshot = AnthropicApiCreditsCollector.ParseSnapshot(
            """{"amount":"17000","pending_invoice_amount_cents":"0"}""",
            """{"remaining_amount_cents":"2500","expires_at":"not-a-date"}""",
            Guid.NewGuid().ToString(),
            "",
            VerifiedAt);

        Assert.AreEqual(17000m, snapshot.AmountCents);
        Assert.AreEqual(2500m, snapshot.ExpiringAmountCents);
        Assert.IsNull(snapshot.ExpiresAt);
    }

    [TestMethod]
    public void MalformedExpiryDoesNotFailPrimaryBalance()
    {
        var snapshot = AnthropicApiCreditsCollector.ParseSnapshot(
            """{"amount":17000}""",
            """{"remaining_amount_cents":""",
            Guid.NewGuid().ToString(),
            "",
            VerifiedAt);

        Assert.AreEqual(17000m, snapshot.AmountCents);
        Assert.IsNull(snapshot.ExpiringAmountCents);
        Assert.IsNull(snapshot.ExpiresAt);
    }

    [TestMethod]
    public void NegativeBalanceRendersAsUnpaidBalance()
    {
        var snapshot = AnthropicApiCreditsCollector.ParseSnapshot(
            """{"amount":-2500}""",
            null,
            Guid.NewGuid().ToString(),
            "",
            VerifiedAt);

        var provider = AnthropicApiCreditsCollector.CreateProviderUsage(snapshot);
        var window = provider.Windows.Single();

        Assert.AreEqual("Unpaid balance $25.00", window.RemainingText);
        Assert.AreEqual(100d, window.Used);
        Assert.AreEqual(0d, window.Remaining);
    }

    [TestMethod]
    public void MissingRequiredAmountIsSchemaFailure()
    {
        var exception = Assert.ThrowsExactly<AnthropicApiCreditsException>(() =>
            AnthropicApiCreditsCollector.ParseSnapshot(
                """{"pending_invoice_amount_cents":1234}""",
                null,
                Guid.NewGuid().ToString(),
                "",
                VerifiedAt));

        Assert.AreEqual(AnthropicApiCreditsFailureKind.Schema, exception.Kind);
    }

    [TestMethod]
    public void HugeAmountIsSchemaFailure()
    {
        var exception = Assert.ThrowsExactly<AnthropicApiCreditsException>(() =>
            AnthropicApiCreditsCollector.ParseSnapshot(
                """{"amount":100000001}""",
                null,
                Guid.NewGuid().ToString(),
                "",
                VerifiedAt));

        Assert.AreEqual(AnthropicApiCreditsFailureKind.Schema, exception.Kind);
    }

    [TestMethod]
    public void HtmlResponseIsAuthExpiredFailure()
    {
        var exception = Assert.ThrowsExactly<AnthropicApiCreditsException>(() =>
            AnthropicApiCreditsCollector.ParseSnapshot(
                "<!doctype html><html><body>Sign in</body></html>",
                null,
                Guid.NewGuid().ToString(),
                "",
                VerifiedAt));

        Assert.AreEqual(AnthropicApiCreditsFailureKind.AuthExpired, exception.Kind);
    }

    [TestMethod]
    public void NullCreditsResponseIsSchemaFailure()
    {
        var exception = Assert.ThrowsExactly<AnthropicApiCreditsException>(() =>
            AnthropicApiCreditsCollector.ParseSnapshot(
                "null",
                null,
                Guid.NewGuid().ToString(),
                "",
                VerifiedAt));

        Assert.AreEqual(AnthropicApiCreditsFailureKind.Schema, exception.Kind);
    }

    [TestMethod]
    public void VisibleBillingPageBalanceCanBeParsedAsFallback()
    {
        var orgUuid = Guid.NewGuid().ToString();
        var snapshot = AnthropicApiCreditsSetupWindow.TryParseVisibleBalanceSnapshot(
            """
            Billing
            Credit balance
            Your credit balance will be consumed with API, Claude Code and Workbench usage.
            $182.82
            Remaining balance · Auto reload on
            """,
            orgUuid,
            "Godly One",
            VerifiedAt);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(18282m, snapshot.AmountCents);
        Assert.AreEqual(orgUuid, snapshot.OrganizationUuid);
        Assert.AreEqual("Godly One", snapshot.OrganizationName);
    }

    [TestMethod]
    public async Task CollectUsesFreshCacheAfterTransientFailure()
    {
        var orgUuid = Guid.NewGuid().ToString();
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            (HttpStatusCode.InternalServerError, """{"error":"down"}""")));
        var settingsService = CreateSettingsService(orgUuid, cacheVerifiedAt: DateTimeOffset.Now.AddHours(-2));
        var collector = new AnthropicApiCreditsCollector(settingsService, httpClient);

        var provider = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(provider.IsUnavailable);
        StringAssert.Contains(provider.StatusMessage, "Showing last verified");
        Assert.AreEqual("$170.00 left", provider.Windows.Single().RemainingText);
        StringAssert.Contains(provider.Windows.Single().Detail, "refresh failed");
    }

    [TestMethod]
    public async Task CollectDoesNotUseExpiredCacheAfterTransientFailure()
    {
        var orgUuid = Guid.NewGuid().ToString();
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            (HttpStatusCode.InternalServerError, """{"error":"down"}""")));
        var settingsService = CreateSettingsService(orgUuid, cacheVerifiedAt: DateTimeOffset.Now.AddDays(-8));
        var collector = new AnthropicApiCreditsCollector(settingsService, httpClient);

        var provider = await collector.CollectAsync(CancellationToken.None);

        Assert.IsTrue(provider.IsUnavailable);
        StringAssert.Contains(provider.StatusMessage, "no recent cached balance");
    }

    [TestMethod]
    public async Task CollectDoesNotUseCacheWhenLoginExpired()
    {
        var orgUuid = Guid.NewGuid().ToString();
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            (HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""")));
        var settingsService = CreateSettingsService(orgUuid, cacheVerifiedAt: DateTimeOffset.Now);
        var collector = new AnthropicApiCreditsCollector(settingsService, httpClient);

        var provider = await collector.CollectAsync(CancellationToken.None);

        Assert.IsTrue(provider.IsUnavailable);
        StringAssert.Contains(provider.StatusMessage, "login expired");
        Assert.AreEqual(0, provider.Windows.Count);
    }

    [TestMethod]
    public async Task CollectSavesCacheAfterSuccessfulRefresh()
    {
        var orgUuid = Guid.NewGuid().ToString();
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            (HttpStatusCode.OK, """{"amount":4321}"""),
            (HttpStatusCode.OK, """{"remaining_amount_cents":1200,"expires_at":"2026-08-01T00:00:00Z"}""")));
        var settingsService = CreateSettingsService(orgUuid, cacheVerifiedAt: null);
        var collector = new AnthropicApiCreditsCollector(settingsService, httpClient);

        var provider = await collector.CollectAsync(CancellationToken.None);
        var savedSettings = settingsService.Load();

        Assert.IsFalse(provider.IsUnavailable);
        Assert.AreEqual("$43.21 left", provider.Windows.Single().RemainingText);
        Assert.IsNotNull(savedSettings.AnthropicApiCreditsLastBalance);
        Assert.AreEqual(4321m, savedSettings.AnthropicApiCreditsLastBalance.AmountCents);
        Assert.AreEqual(1200m, savedSettings.AnthropicApiCreditsLastBalance.ExpiringAmountCents);
    }

    private static AppSettingsService CreateSettingsService(string orgUuid, DateTimeOffset? cacheVerifiedAt)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var settingsService = new AppSettingsService(tempDirectory);
        settingsService.Save(new AppSettings
        {
            AnthropicApiCreditsCookieHeaderProtected = ProtectedStringService.Protect("sessionKey=test"),
            AnthropicApiCreditsOrganizationUuid = orgUuid,
            AnthropicApiCreditsOrganizationName = "Seth",
            AnthropicApiCreditsLastBalance = cacheVerifiedAt is { } verifiedAt
                ? new AnthropicApiCreditsBalanceCache
                {
                    AmountCents = 17000m,
                    VerifiedAt = verifiedAt,
                    OrganizationUuid = orgUuid,
                    OrganizationName = "Seth"
                }
                : null,
            EnabledProviders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [KnownProviders.AnthropicApiCredits] = true
            }
        });

        return settingsService;
    }

    private sealed class QueueHttpMessageHandler(params (HttpStatusCode StatusCode, string Content)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Content)> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected request to {request.RequestUri}.");
            }

            var (statusCode, content) = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
        }
    }
}
