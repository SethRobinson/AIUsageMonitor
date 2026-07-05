using System.Net;
using System.Net.Http;
using System.IO;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ClaudeStatusFileUsageCollectorTests
{
    [TestMethod]
    public async Task FreshLocalStatusExportIsUsedBeforeOAuth()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        WriteClaudeCredentialsAndStatusExport(homeDirectory, DateTimeOffset.Now);
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.AreEqual(KnownProviders.Anthropic, usage.Name);
        Assert.IsFalse(usage.IsUnavailable);
        StringAssert.Contains(usage.StatusMessage, "local status output");
        Assert.AreEqual(2, usage.Windows.Count);
        Assert.AreEqual(42, usage.Windows.Single(window => window.Title == "5h").Used);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task ManualRefreshUsesLiveOAuthPlanTierWhenLocalStatusIsFresh()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        var fiveHourResetAt = DateTimeOffset.Now.AddHours(3);
        var sevenDayResetAt = DateTimeOffset.Now.AddDays(6);
        WriteClaudeCredentialsAndStatusExport(
            homeDirectory,
            DateTimeOffset.Now,
            subscriptionType: "max",
            rateLimitTier: "max_5x",
            includePlanInStatusExport: true);
        var handler = new QueueHttpMessageHandler(
            (HttpStatusCode.OK, BuildOAuthProfileResponse("default_claude_max_20x")),
            (HttpStatusCode.OK, BuildOAuthUsageResponse(fiveHourResetAt, sevenDayResetAt)));
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(forceRefresh: true, CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual("Claude OAuth usage endpoint", usage.Source);
        Assert.AreEqual("Max 20x", usage.PlanName);
        Assert.AreEqual(2, handler.CallCount);
    }

    [TestMethod]
    public async Task ManualRefreshCachesLivePlanTierForNextStartup()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        WriteClaudeCredentialsAndStatusExport(
            homeDirectory,
            DateTimeOffset.Now,
            subscriptionType: "max",
            rateLimitTier: "max_5x",
            includePlanInStatusExport: true);
        var handler = new QueueHttpMessageHandler(
            (HttpStatusCode.OK, BuildOAuthProfileResponse("default_claude_max_20x")),
            (HttpStatusCode.OK, BuildOAuthUsageResponse(DateTimeOffset.Now.AddHours(3), DateTimeOffset.Now.AddDays(6))));
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var refreshedUsage = await collector.CollectAsync(forceRefresh: true, CancellationToken.None);

        Assert.AreEqual("Max 20x", refreshedUsage.PlanName);

        var restartHandler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var restartHttpClient = new HttpClient(restartHandler);
        var restartedCollector = new ClaudeStatusFileUsageCollector(homeDirectory, restartHttpClient);

        var startupUsage = await restartedCollector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(startupUsage.IsUnavailable);
        Assert.AreEqual("Max 20x", startupUsage.PlanName);
        StringAssert.Contains(startupUsage.StatusMessage, "local status output");
        Assert.AreEqual(0, restartHandler.CallCount);
    }

    [TestMethod]
    public async Task CachedOAuthProfilePlanOverridesFreshStatusLineTier()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        WriteClaudeCredentialsAndStatusExport(
            homeDirectory,
            DateTimeOffset.Now,
            subscriptionType: "max",
            rateLimitTier: "max_5x",
            includePlanInStatusExport: true);
        WriteClaudeProfileCache(homeDirectory, "Max 20x");
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual("Max 20x", usage.PlanName);
        StringAssert.Contains(usage.StatusMessage, "local status output");
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task OAuthRateLimitWithStaleLocalStatusExportReportsUnavailableWithoutWindows()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        WriteClaudeCredentialsAndStatusExport(homeDirectory, DateTimeOffset.Now.AddHours(-1));
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.AreEqual(KnownProviders.Anthropic, usage.Name);
        Assert.IsTrue(usage.IsUnavailable);
        StringAssert.Contains(usage.StatusMessage, "stale");
        StringAssert.Contains(usage.StatusMessage, "rate-limited");
        Assert.IsFalse(usage.StatusMessage.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, usage.Windows.Count);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task PassedResetInFreshLocalStatusFallsBackToOAuthUsage()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        var fiveHourResetAt = DateTimeOffset.Now.AddHours(3);
        var sevenDayResetAt = DateTimeOffset.Now.AddDays(6);
        WriteClaudeCredentialsAndStatusExport(
            homeDirectory,
            DateTimeOffset.Now,
            fiveHourUsedPercent: 0,
            fiveHourResetAt: DateTimeOffset.Now.AddHours(-2),
            sevenDayUsedPercent: 6,
            sevenDayResetAt: DateTimeOffset.Now.AddDays(6));
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            BuildOAuthUsageResponse(fiveHourResetAt, sevenDayResetAt));
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual("Claude OAuth usage endpoint", usage.Source);
        Assert.AreEqual(3, usage.Windows.Count);

        var fiveHour = usage.Windows.Single(window => window.Title == "5h");
        Assert.AreEqual(15, fiveHour.Used);
        Assert.AreEqual(fiveHourResetAt, fiveHour.ResetAt);

        var sevenDay = usage.Windows.Single(window => window.Title == "7d");
        Assert.AreEqual(9, sevenDay.Used);
        Assert.AreEqual(sevenDayResetAt, sevenDay.ResetAt);

        var sonnet = usage.Windows.Single(window => window.Title == "Sonnet");
        Assert.AreEqual(2, sonnet.Used);
        Assert.AreEqual(sevenDayResetAt, sonnet.ResetAt);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task LocalStatusExportWithNullRateLimitsDoesNotThrow()
    {
        // The status-line exporter writes "rate_limits": null when Claude Code has not
        // surfaced rate_limits yet. Parsing must degrade gracefully, not throw
        // InvalidOperationException (which would fail the whole card and back off).
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);
        File.WriteAllText(
            Path.Combine(claudeDirectory, ".credentials.json"),
            """
            {
              "claudeAiOauth": {
                "accessToken": "test-token",
                "subscriptionType": "pro",
                "rateLimitTier": "default"
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(claudeDirectory, "ai-usage-monitor-usage.json"),
            $$"""
            {
              "generatedAt": "{{DateTimeOffset.Now:O}}",
              "status": "missing_rate_limits",
              "rate_limits": null
            }
            """);
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.AreEqual(KnownProviders.Anthropic, usage.Name);
        Assert.IsTrue(usage.IsUnavailable);
        Assert.AreEqual(0, usage.Windows.Count);
    }

    private static void WriteClaudeCredentialsAndStatusExport(
        string homeDirectory,
        DateTimeOffset generatedAt,
        double fiveHourUsedPercent = 42,
        DateTimeOffset? fiveHourResetAt = null,
        double sevenDayUsedPercent = 17,
        DateTimeOffset? sevenDayResetAt = null,
        string subscriptionType = "pro",
        string rateLimitTier = "default",
        bool includePlanInStatusExport = false)
    {
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);

        File.WriteAllText(
            Path.Combine(claudeDirectory, ".credentials.json"),
            $$"""
            {
              "claudeAiOauth": {
                "accessToken": "test-token",
                "subscriptionType": "{{subscriptionType}}",
                "rateLimitTier": "{{rateLimitTier}}"
              }
            }
            """);

        fiveHourResetAt ??= DateTimeOffset.Now.AddHours(1);
        sevenDayResetAt ??= DateTimeOffset.Now.AddHours(1);
        var statusPlanFields = includePlanInStatusExport
            ? $$"""
              "subscriptionType": "{{subscriptionType}}",
              "rateLimitTier": "{{rateLimitTier}}",
            """
            : string.Empty;
        File.WriteAllText(
            Path.Combine(claudeDirectory, "ai-usage-monitor-usage.json"),
            $$"""
            {
              "generatedAt": "{{generatedAt:O}}",
            {{statusPlanFields}}
              "rate_limits": {
                "five_hour": {
                  "used_percentage": {{fiveHourUsedPercent}},
                  "resets_at": {{fiveHourResetAt.Value.ToUnixTimeSeconds()}}
                },
                "seven_day": {
                  "used_percentage": {{sevenDayUsedPercent}},
                  "resets_at": {{sevenDayResetAt.Value.ToUnixTimeSeconds()}}
                }
              }
            }
            """);
    }

    private static void WriteClaudeProfileCache(string homeDirectory, string planName)
    {
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);
        File.WriteAllText(
            Path.Combine(claudeDirectory, "ai-usage-monitor-profile.json"),
            $$"""
            {
              "planName": "{{planName}}",
              "cachedAt": "{{DateTimeOffset.Now:O}}"
            }
            """);
    }

    private static string BuildOAuthUsageResponse(DateTimeOffset fiveHourResetAt, DateTimeOffset sevenDayResetAt)
    {
        return $$"""
        {
          "limits": [
            {
              "kind": "session",
              "group": "session",
              "percent": 15,
              "resets_at": "{{fiveHourResetAt:O}}"
            },
            {
              "kind": "weekly_all",
              "group": "weekly",
              "percent": 9,
              "resets_at": "{{sevenDayResetAt:O}}"
            },
            {
              "kind": "weekly_scoped",
              "group": "weekly",
              "percent": 2,
              "resets_at": "{{sevenDayResetAt:O}}",
              "scope": {
                "model": {
                  "display_name": "Sonnet"
                }
              }
            }
          ]
        }
        """;
    }

    private static string BuildOAuthProfileResponse(string rateLimitTier)
    {
        return $$"""
        {
          "account": {
            "has_claude_max": true,
            "has_claude_pro": false
          },
          "organization": {
            "organization_type": "claude_max",
            "rate_limit_tier": "{{rateLimitTier}}"
          }
        }
        """;
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string content = "") : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var response = new HttpResponseMessage(statusCode);
            if (!string.IsNullOrWhiteSpace(content))
            {
                response.Content = new StringContent(content);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class QueueHttpMessageHandler(params (HttpStatusCode StatusCode, string Content)[] responses) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var callIndex = Interlocked.Increment(ref _callCount) - 1;
            var responseSpec = callIndex < responses.Length
                ? responses[callIndex]
                : responses[^1];
            var response = new HttpResponseMessage(responseSpec.StatusCode);
            if (!string.IsNullOrWhiteSpace(responseSpec.Content))
            {
                response.Content = new StringContent(responseSpec.Content);
            }

            return Task.FromResult(response);
        }
    }
}
