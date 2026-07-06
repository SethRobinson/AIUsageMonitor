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
    public async Task FreshMaxLocalStatusExportCollectsOAuthScopedModelCard()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        WriteClaudeCredentialsAndStatusExport(
            homeDirectory,
            DateTimeOffset.Now,
            subscriptionType: "max",
            rateLimitTier: "max_20x");
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            BuildOAuthUsageResponseWithExtraUsage(DateTimeOffset.Now.AddHours(3), DateTimeOffset.Now.AddDays(6)));
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual("Claude OAuth usage endpoint", usage.Source);
        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual("Fable", usage.Windows.Single(window => window.Title == "Fable").DisplayGroupName);
        Assert.AreEqual("Fable", usage.Windows.Single(window => window.Title == "Extra usage").DisplayGroupName);
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
        Assert.AreEqual(1, restartHandler.CallCount);
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
        Assert.AreEqual(1, handler.CallCount);
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
        Assert.AreEqual("Sonnet", sonnet.DisplayGroupName);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task OAuthUsageParsesExtraUsageCreditsAsGroupedWindow()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        WriteClaudeCredentials(homeDirectory);
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            BuildOAuthUsageResponseWithExtraUsage(DateTimeOffset.Now.AddHours(3), DateTimeOffset.Now.AddDays(6)));
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual("Claude OAuth usage endpoint", usage.Source);

        var fable = usage.Windows.Single(window => window.Title == "Fable");
        Assert.AreEqual("Fable", fable.Title);
        Assert.AreEqual("Weekly model limit", fable.Detail);

        var credits = usage.Windows.Single(window => window.Title == "Extra usage");
        Assert.AreEqual("Extra usage", credits.Title);
        Assert.AreEqual("Fable", credits.DisplayGroupName);
        Assert.AreEqual(150, credits.Limit);
        Assert.AreEqual(20.61, credits.Used);
        Assert.AreEqual(129.39, credits.Remaining);
        Assert.AreEqual("$20.61 of $150", credits.RemainingText);
        Assert.AreEqual("Monthly spend limit", credits.Detail);
        Assert.IsTrue(credits.HideReset);
    }

    [TestMethod]
    public async Task FreshLocalStatusExportWithLimitsPreservesScopedModelCardBeforeOAuth()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        var fiveHourResetAt = DateTimeOffset.Now.AddHours(3);
        var sevenDayResetAt = DateTimeOffset.Now.AddDays(6);
        WriteClaudeCredentials(homeDirectory);
        WriteClaudeStatusExportWithLimitsAndExtraUsage(homeDirectory, DateTimeOffset.Now, fiveHourResetAt, sevenDayResetAt);
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        StringAssert.Contains(usage.StatusMessage, "local status output");
        Assert.AreEqual(0, handler.CallCount);

        var fable = usage.Windows.Single(window => window.Title == "Fable");
        Assert.AreEqual("Fable", fable.DisplayGroupName);
        Assert.AreEqual("Weekly model limit", fable.Detail);
        Assert.AreEqual(100, fable.Used);

        var credits = usage.Windows.Single(window => window.Title == "Extra usage");
        Assert.AreEqual("Fable", credits.DisplayGroupName);
        Assert.AreEqual("$20.61 of $150", credits.RemainingText);
    }

    [TestMethod]
    public async Task CachedOAuthScopedModelCardSurvivesBaseOnlyLocalStatusWhenOAuthIsRateLimited()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        WriteClaudeCredentials(homeDirectory);
        var handler = new QueueHttpMessageHandler(
            (HttpStatusCode.OK, BuildOAuthUsageResponseWithExtraUsage(DateTimeOffset.Now.AddHours(3), DateTimeOffset.Now.AddDays(6))),
            (HttpStatusCode.TooManyRequests, string.Empty));
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var oauthUsage = await collector.CollectAsync(CancellationToken.None);
        Assert.AreEqual("Fable", oauthUsage.Windows.Single(window => window.Title == "Fable").DisplayGroupName);

        WriteClaudeCredentialsAndStatusExport(
            homeDirectory,
            DateTimeOffset.Now,
            subscriptionType: "max",
            rateLimitTier: "max_20x");

        var mergedUsage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(mergedUsage.IsUnavailable);
        StringAssert.Contains(mergedUsage.StatusMessage, "local status output");
        StringAssert.Contains(mergedUsage.StatusMessage, "last successful Claude OAuth usage check");
        Assert.AreEqual(2, handler.CallCount);
        Assert.AreEqual("Fable", mergedUsage.Windows.Single(window => window.Title == "Fable").DisplayGroupName);
        Assert.AreEqual("Fable", mergedUsage.Windows.Single(window => window.Title == "Extra usage").DisplayGroupName);
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

    [TestMethod]
    public async Task CachedOAuthUsageIsUsedWhenCredentialsAreEmptyAndStatusLineHasNoLimits()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        WriteEmptyClaudeCredentials(homeDirectory);
        WriteClaudeStatusExportWithoutLimits(homeDirectory, DateTimeOffset.Now);
        WriteLegacyClaudeStatusExport(homeDirectory, DateTimeOffset.Now.AddDays(-50));
        WriteCachedOAuthUsage(homeDirectory, DateTimeOffset.Now, DateTimeOffset.Now.AddHours(3), DateTimeOffset.Now.AddDays(6));
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, BuildOAuthUsageResponse(DateTimeOffset.Now.AddHours(3), DateTimeOffset.Now.AddDays(6)));
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual("Max 20x", usage.PlanName);
        Assert.AreEqual("Claude OAuth usage endpoint cache", usage.Source);
        StringAssert.Contains(usage.StatusMessage, "last successful Claude OAuth usage check");
        Assert.IsFalse(usage.StatusMessage.Contains("stale", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(4, usage.Windows.Count);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task FreshMissingLimitsStatusBeatsStaleLegacyExport()
    {
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        WriteClaudeCredentials(homeDirectory);
        WriteClaudeStatusExportWithoutLimits(homeDirectory, DateTimeOffset.Now);
        WriteLegacyClaudeStatusExport(homeDirectory, DateTimeOffset.Now.AddDays(-50));
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsTrue(usage.IsUnavailable);
        StringAssert.Contains(usage.StatusMessage, "rate_limits was absent");
        Assert.IsFalse(usage.StatusMessage.Contains("stale", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, handler.CallCount);
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

    private static void WriteEmptyClaudeCredentials(string homeDirectory)
    {
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);
        File.WriteAllText(Path.Combine(claudeDirectory, ".credentials.json"), "{}");
    }

    private static void WriteClaudeStatusExportWithoutLimits(string homeDirectory, DateTimeOffset generatedAt)
    {
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);
        File.WriteAllText(
            Path.Combine(claudeDirectory, "ai-usage-monitor-usage.json"),
            $$"""
            {
              "generatedAt": "{{generatedAt:O}}",
              "source": "Claude Code statusLine",
              "status": "missing_rate_limits",
              "statusMessage": "Claude status line ran, but rate_limits was absent.",
              "rate_limits": null,
              "limits": null
            }
            """);
    }

    private static void WriteLegacyClaudeStatusExport(string homeDirectory, DateTimeOffset generatedAt)
    {
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);
        File.WriteAllText(
            Path.Combine(claudeDirectory, "apimonitor-usage.json"),
            $$"""
            {
              "generatedAt": "{{generatedAt:O}}",
              "rate_limits": {
                "five_hour": {
                  "used_percentage": 99,
                  "resets_at": {{DateTimeOffset.Now.AddHours(1).ToUnixTimeSeconds()}}
                }
              }
            }
            """);
    }

    private static void WriteCachedOAuthUsage(
        string homeDirectory,
        DateTimeOffset cachedAt,
        DateTimeOffset fiveHourResetAt,
        DateTimeOffset sevenDayResetAt)
    {
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);
        File.WriteAllText(
            Path.Combine(claudeDirectory, "ai-usage-monitor-oauth-usage-cache.json"),
            $$"""
            {
              "CachedAt": "{{cachedAt:O}}",
              "Usage": {
                "Name": "Anthropic",
                "PlanName": "Max 20x",
                "Source": "Claude OAuth usage endpoint",
                "StatusMessage": "Claude Max 20x quota from local Claude Code OAuth credentials.",
                "Windows": [
                  {
                    "Title": "5h",
                    "Limit": 100,
                    "Used": 12,
                    "Remaining": 88,
                    "ResetAt": "{{fiveHourResetAt:O}}"
                  },
                  {
                    "Title": "7d",
                    "Limit": 100,
                    "Used": 54,
                    "Remaining": 46,
                    "ResetAt": "{{sevenDayResetAt:O}}"
                  },
                  {
                    "Title": "Fable",
                    "DisplayGroupName": "Fable",
                    "Limit": 100,
                    "Used": 100,
                    "Remaining": 0,
                    "ResetAt": "{{sevenDayResetAt:O}}",
                    "Detail": "Weekly model limit"
                  },
                  {
                    "Title": "Extra usage",
                    "DisplayGroupName": "Fable",
                    "Limit": 150,
                    "Used": 29.39,
                    "Remaining": 120.61,
                    "RemainingText": "$29.39 of $150",
                    "Detail": "Monthly spend limit",
                    "HideReset": true
                  }
                ]
              }
            }
            """);
    }

    private static void WriteClaudeStatusExportWithLimitsAndExtraUsage(
        string homeDirectory,
        DateTimeOffset generatedAt,
        DateTimeOffset fiveHourResetAt,
        DateTimeOffset sevenDayResetAt)
    {
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);
        File.WriteAllText(
            Path.Combine(claudeDirectory, "ai-usage-monitor-usage.json"),
            $$"""
            {
              "generatedAt": "{{generatedAt:O}}",
              "source": "Claude Code statusLine",
              "status": "ok",
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
                  "percent": 100,
                  "resets_at": "{{sevenDayResetAt:O}}",
                  "scope": {
                    "model": {
                      "display_name": "Fable"
                    }
                  }
                }
              ],
              "extra_usage": {
                "is_enabled": true,
                "monthly_limit": 15000,
                "used_credits": 2061,
                "currency": "USD"
              }
            }
            """);
    }

    private static void WriteClaudeCredentials(string homeDirectory)
    {
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);

        File.WriteAllText(
            Path.Combine(claudeDirectory, ".credentials.json"),
            """
            {
              "claudeAiOauth": {
                "accessToken": "test-token",
                "subscriptionType": "max",
                "rateLimitTier": "max_20x"
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

    private static string BuildOAuthUsageResponseWithExtraUsage(DateTimeOffset fiveHourResetAt, DateTimeOffset sevenDayResetAt)
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
              "percent": 100,
              "resets_at": "{{sevenDayResetAt:O}}",
              "scope": {
                "model": {
                  "display_name": "Fable"
                }
              }
            }
          ],
          "extra_usage": {
            "is_enabled": true,
            "monthly_limit": 15000,
            "used_credits": 2061,
            "balance": 12061,
            "currency": "USD"
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
