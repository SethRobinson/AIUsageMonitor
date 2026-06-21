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

    private static void WriteClaudeCredentialsAndStatusExport(string homeDirectory, DateTimeOffset generatedAt)
    {
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

        var resetAt = DateTimeOffset.Now.AddHours(1).ToUnixTimeSeconds();
        File.WriteAllText(
            Path.Combine(claudeDirectory, "ai-usage-monitor-usage.json"),
            $$"""
            {
              "generatedAt": "{{generatedAt:O}}",
              "rate_limits": {
                "five_hour": {
                  "used_percentage": 42,
                  "resets_at": {{resetAt}}
                },
                "seven_day": {
                  "used_percentage": 17,
                  "resets_at": {{resetAt}}
                }
              }
            }
            """);
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
