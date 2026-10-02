using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ClaudeUsageCreditsTests
{
    private string _homeDirectory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ClaudeDirectory);
        File.WriteAllText(Path.Combine(ClaudeDirectory, ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"test-token","subscriptionType":"pro"}}""");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_homeDirectory, recursive: true);

    private string ClaudeDirectory => Path.Combine(_homeDirectory, ".claude");

    [TestMethod]
    [DataRow(11385, "USD", 2, "$113.85 credits")]
    [DataRow(0, "USD", 2, "$0.00 credits")]
    [DataRow(11385, "JPY", 0, "11385 JPY credits")]
    [DataRow(113850, "KWD", 3, "113.850 KWD credits")]
    public void BalanceUsesReportedCurrencyAndExponent(int minor, string currency, int exponent, string expected)
    {
        using var document = JsonSerializer.SerializeToDocument(new
        {
            spend = new { balance = new { amount_minor = minor, currency, exponent } }
        });
        var windows = new List<UsageWindow>();

        ClaudeUsageCredits.AddBalance(windows, document.RootElement);

        var balance = windows.Single();
        Assert.AreEqual(expected, balance.RemainingText);
        Assert.AreEqual(string.Empty, balance.DisplayGroupName);
        Assert.IsTrue(balance.IsBalance);
        Assert.IsTrue(balance.HideReset);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("11385")]
    [DataRow("{}")]
    [DataRow("{\"amount_minor\":null,\"currency\":\"USD\",\"exponent\":2}")]
    [DataRow("{\"amount_minor\":-100,\"currency\":\"USD\",\"exponent\":2}")]
    [DataRow("{\"amount_minor\":1e100,\"currency\":\"USD\",\"exponent\":2}")]
    [DataRow("{\"amount_minor\":11385,\"currency\":\"USD\"}")]
    [DataRow("{\"amount_minor\":11385,\"exponent\":2}")]
    [DataRow("{\"amount_minor\":11385,\"currency\":\"bad currency\",\"exponent\":2}")]
    [DataRow("{\"amount_minor\":11385,\"currency\":\"USD\",\"exponent\":-2}")]
    [DataRow("{\"amount_minor\":11385,\"currency\":\"USD\",\"exponent\":100}")]
    public async Task MalformedOptionalBalanceDoesNotBreakQuotaCollection(string balanceJson)
    {
        var response = UsageResponse();
        response["spend"] = new JsonObject { ["balance"] = JsonNode.Parse(balanceJson) };
        using var client = new HttpClient(new QueueHandler((HttpStatusCode.OK, response.ToJsonString())));
        var usage = await CreateCollector(client).CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.IsFalse(usage.Windows.Any(window => window.IsBalance));
        Assert.AreEqual(69, usage.Windows.Single(window => window.Title == "7d").Used);
    }

    [TestMethod]
    public async Task AccountBalanceAppearsOnlyOnItsMainCardAndDoesNotChangeQuotaStatus()
    {
        var response = UsageResponse(11385);
        response["seven_day_sonnet"] = new JsonObject
        {
            ["utilization"] = 100,
            ["resets_at"] = DateTimeOffset.Now.AddDays(2).ToString("O")
        };
        using var client = new HttpClient(new QueueHandler((HttpStatusCode.OK, response.ToJsonString())));
        var account = new ProviderAccount { ProviderName = KnownProviders.Anthropic, Id = "personal", Label = "Personal" };
        var collector = new AccountScopedUsageCollector(CreateCollector(client), account.DisplayKey, KnownProviders.Anthropic, account.CardLabel);
        var usage = await collector.CollectAsync(CancellationToken.None);
        var viewModel = new UsageOverlayViewModel();
        viewModel.ApplyProvider(usage);
        viewModel.ApplyProvider(new ProviderUsage
        {
            Name = "Anthropic - Work",
            Windows = [ProviderUsageFactory.PercentWindow("7d", 4, null)]
        });

        var personal = viewModel.Providers.Single(card => card.ShortName == account.DisplayKey);
        Assert.AreEqual("$113.85 credits", personal.SecondaryBalanceText);
        Assert.AreEqual(31, personal.PrimaryRemainingPercent);
        Assert.IsTrue(personal.Windows.Single(window => window.IsBalance).IsHighlightedBalance);
        Assert.IsFalse(personal.Windows.Single(window => window.IsBalance).ShowsProgress);
        Assert.AreEqual(3, viewModel.Providers.Count, "credits must not create another provider/model card");
        Assert.IsTrue(viewModel.Providers.Where(card => card != personal).All(card => !card.HasSecondaryBalance));
    }

    [TestMethod]
    public async Task ZeroCreditBalanceDoesNotMarkHealthyQuotaExhausted()
    {
        using var client = new HttpClient(new QueueHandler((HttpStatusCode.OK, UsageResponse(0).ToJsonString())));
        var card = new ProviderUsageCard(await CreateCollector(client).CollectAsync(CancellationToken.None));

        Assert.AreEqual(31, card.PrimaryRemainingPercent);
        Assert.AreEqual("$0.00 credits", card.SecondaryBalanceText);
        Assert.AreNotEqual("Exhausted", card.OverallStatusLabel);
    }

    [TestMethod]
    public async Task SpendLimitMinusUsedIsNeverPresentedAsThePrepaidBalance()
    {
        var response = UsageResponse();
        response["extra_usage"] = JsonNode.Parse("""
            {"is_enabled":true,"monthly_limit":15000,"used_credits":2061,"currency":"USD"}
            """);
        response["spend"] = JsonNode.Parse("""
            {"enabled":true,"used":{"amount_minor":2061,"currency":"USD","exponent":2},
             "limit":{"amount_minor":15000,"currency":"USD","exponent":2}}
            """);
        using var client = new HttpClient(new QueueHandler((HttpStatusCode.OK, response.ToJsonString())));
        var usage = await CreateCollector(client).CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.Windows.Any(window => window.IsBalance));
        Assert.AreEqual("$20.61 of $150", usage.Windows.Single(window => window.Title == "Extra usage").RemainingText);
    }

    [TestMethod]
    public async Task DisabledSpendingWithNullBalanceDoesNotInventAZeroCreditBalance()
    {
        // Sanitized fields from a successful OAuth response for an account whose
        // Claude Billing page shows purchased credits. Spent zero is not balance zero.
        var response = UsageResponse();
        response["spend"] = JsonNode.Parse("""
            {"used":{"amount_minor":0,"currency":"USD","exponent":2},
             "limit":null,"percent":0,"severity":"normal","enabled":false,
             "balance":null,"auto_reload":null,"can_purchase_credits":false,"can_toggle":false}
            """);
        response["extra_usage"] = JsonNode.Parse("""
            {"is_enabled":false,"monthly_limit":null,"used_credits":null,
             "currency":null,"decimal_places":null,"user_disabled":true,
             "spend_limit_reached":false,"credits_ever_enabled":true}
            """);
        using var client = new HttpClient(new QueueHandler((HttpStatusCode.OK, response.ToJsonString())));
        var usage = await CreateCollector(client).CollectAsync(CancellationToken.None);
        var card = new ProviderUsageCard(usage);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.IsFalse(usage.Windows.Any(window => window.IsBalance));
        Assert.IsFalse(card.HasSecondaryBalance);
        Assert.AreEqual(31, card.PrimaryRemainingPercent);
    }

    [TestMethod]
    public async Task PurchasedBalanceRemainsVisibleWhenExtraUsageIsDisabled()
    {
        var response = UsageResponse(11385);
        response["spend"]!["enabled"] = false;
        using var client = new HttpClient(new QueueHandler((HttpStatusCode.OK, response.ToJsonString())));
        var usage = await CreateCollector(client).CollectAsync(CancellationToken.None);

        var balance = usage.Windows.Single(window => window.IsBalance);
        Assert.AreEqual("$113.85 credits", balance.RemainingText);
        StringAssert.Contains(balance.Detail, "disabled");
    }

    [TestMethod]
    public async Task CachedBalanceSurvivesLocalModelExportAndOAuthRateLimit()
    {
        var initial = UsageResponse(11385);
        using var client = new HttpClient(new QueueHandler(
            (HttpStatusCode.OK, initial.ToJsonString()), (HttpStatusCode.TooManyRequests, "")));
        var collector = CreateCollector(client);
        await collector.CollectAsync(CancellationToken.None);
        // A fresh export can have a model-specific window while omitting the balance.
        var local = new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
            ["limits"] = new JsonArray(
                new JsonObject { ["kind"] = "weekly_all", ["percent"] = 69 },
                new JsonObject { ["kind"] = "weekly_scoped", ["percent"] = 100,
                    ["scope"] = new JsonObject { ["model"] = new JsonObject { ["display_name"] = "Fable" } } })
        };
        File.WriteAllText(Path.Combine(ClaudeDirectory, "ai-usage-monitor-usage.json"), local.ToJsonString());

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual("$113.85 credits", usage.Windows.Single(window => window.IsBalance).RemainingText);
        Assert.AreEqual(1, usage.Windows.Count(window => window.Title == "Fable"));
        StringAssert.Contains(usage.StatusMessage, "last successful Claude OAuth usage check");
    }

    [TestMethod]
    public async Task FreshBalanceInLocalExportIsNotReplacedOrDuplicatedByOlderCache()
    {
        using var client = new HttpClient(new QueueHandler((HttpStatusCode.OK, UsageResponse(11385).ToJsonString())));
        var collector = CreateCollector(client);
        await collector.CollectAsync(CancellationToken.None);
        var local = UsageResponse(10123);
        local["generatedAt"] = DateTimeOffset.Now.ToString("O");
        local["rate_limits"] = new JsonObject
        {
            ["seven_day"] = new JsonObject { ["used_percentage"] = 69 }
        };
        File.WriteAllText(Path.Combine(ClaudeDirectory, "ai-usage-monitor-usage.json"), local.ToJsonString());

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.AreEqual("$101.23 credits", usage.Windows.Single(window => window.IsBalance).RemainingText);
    }

    [TestMethod]
    public async Task NewAuthoritativeResponseWithoutBalanceReplacesOlderCachedBalance()
    {
        using var client = new HttpClient(new QueueHandler(
            (HttpStatusCode.OK, UsageResponse(11385).ToJsonString()),
            (HttpStatusCode.OK, UsageResponse().ToJsonString()),
            (HttpStatusCode.TooManyRequests, "")));
        var collector = CreateCollector(client);
        await collector.CollectAsync(CancellationToken.None);
        await collector.CollectAsync(CancellationToken.None);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.IsFalse(usage.Windows.Any(window => window.IsBalance), "an older balance must not reappear after it is omitted by a successful response");
    }

    private ClaudeStatusFileUsageCollector CreateCollector(HttpClient client) =>
        new(_homeDirectory, client, allowCliRefresh: false);

    private static JsonObject UsageResponse(int? minor = null)
    {
        var response = new JsonObject
        {
            ["five_hour"] = new JsonObject { ["utilization"] = 7, ["resets_at"] = DateTimeOffset.Now.AddHours(2).ToString("O") },
            ["seven_day"] = new JsonObject { ["utilization"] = 69, ["resets_at"] = DateTimeOffset.Now.AddDays(2).ToString("O") }
        };
        if (minor.HasValue)
        {
            response["spend"] = new JsonObject
            {
                ["enabled"] = true,
                ["balance"] = new JsonObject { ["amount_minor"] = minor.Value, ["currency"] = "USD", ["exponent"] = 2 }
            };
        }
        return response;
    }

    private sealed class QueueHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.AbsoluteUri);
            Assert.IsTrue(_responses.Count > 0, "unexpected extra network request");
            var response = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body) });
        }
    }
}
