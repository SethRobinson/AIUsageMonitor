using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProviderLocaleParsingTests
{
    [TestMethod]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    public async Task ClaudeLocalJsonStringNumbersDoNotDependOnCurrentCulture(string cultureName)
    {
        using var culture = CultureScope.Use(cultureName);
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);
        WriteClaudeCredentials(claudeDirectory);

        var resetAt = DateTimeOffset.Now.AddHours(3);
        File.WriteAllText(
            Path.Combine(claudeDirectory, "ai-usage-monitor-usage.json"),
            $$"""
            {
              "generatedAt": "{{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)}}",
              "rate_limits": {
                "five_hour": {
                  "used_percentage": "79.5",
                  "resets_at": "{{resetAt.ToString("O", CultureInfo.InvariantCulture)}}"
                },
                "seven_day": {
                  "used_percent": "66.5",
                  "resets_at": "{{resetAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}}"
                }
              }
            }
            """);

        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual(0, handler.CallCount);
        Assert.AreEqual(79.5, usage.Windows.Single(window => window.Title == "5h").Used, 0.001);
        Assert.AreEqual(66.5, usage.Windows.Single(window => window.Title == "7d").Used, 0.001);
    }

    [TestMethod]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    public async Task ClaudeLegacyTextStringNumbersDoNotDependOnCurrentCulture(string cultureName)
    {
        using var culture = CultureScope.Use(cultureName);
        var homeDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);
        WriteClaudeCredentials(claudeDirectory);
        File.WriteAllText(
            Path.Combine(claudeDirectory, "usage-status.md"),
            "5h = 79.5%" + Environment.NewLine + "7d = 66.5%");

        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        var collector = new ClaudeStatusFileUsageCollector(homeDirectory, httpClient);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual(0, handler.CallCount);
        Assert.AreEqual(79.5, usage.Windows.Single(window => window.Title == "5h").Used, 0.001);
        Assert.AreEqual(66.5, usage.Windows.Single(window => window.Title == "7d").Used, 0.001);
    }

    [TestMethod]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    public async Task CursorCurrentPeriodStringNumbersDoNotDependOnCurrentCulture(string cultureName)
    {
        using var culture = CultureScope.Use(cultureName);
        var settingsService = CreateCursorSettingsService();
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            (request =>
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                StringAssert.Contains(request.RequestUri?.ToString(), "/auth/full_stripe_profile");
            },
            HttpStatusCode.OK,
            """
            {
              "membershipType": "pro",
              "subscriptionStatus": "active",
              "isOnBillableAuto": false
            }
            """),
            (request =>
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                StringAssert.Contains(request.RequestUri?.ToString(), "/aiserver.v1.DashboardService/GetCurrentPeriodUsage");
            },
            HttpStatusCode.OK,
            """
            {
              "billingCycleEnd": "1783560138855",
              "planUsage": {
                "totalPercentUsed": "42.5",
                "autoPercentUsed": "12.5",
                "apiPercentUsed": "65.5"
              }
            }
            """)));
        var collector = new CursorUsageCollector(
            settingsService,
            httpClient,
            new StaticCursorDesktopAuthSource(new CursorUsageCollector.CursorDesktopAuth(
                "desktop-token",
                "https://api2.cursor.sh",
                "pro",
                "active")));

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual("Pro", usage.PlanName);
        Assert.AreEqual(42.5, usage.Windows.Single(window => window.Title == "Included usage").UsedPercent, 0.001);
        Assert.AreEqual(12.5, usage.Windows.Single(window => window.Title == "Auto models").UsedPercent, 0.001);
        Assert.AreEqual(65.5, usage.Windows.Single(window => window.Title == "API usage").UsedPercent, 0.001);
    }

    private static void WriteClaudeCredentials(string claudeDirectory)
    {
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
    }

    private static AppSettingsService CreateCursorSettingsService()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var settingsService = new AppSettingsService(tempDirectory);
        settingsService.Save(new AppSettings
        {
            EnabledProviders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [KnownProviders.Cursor] = true
            },
            CursorUsageMode = AppSettings.CursorUsageModePersonal,
            CursorDashboardCookieHeaderProtected = ProtectedStringService.Protect("session=test")
        });
        return settingsService;
    }

    private sealed class StaticCursorDesktopAuthSource(CursorUsageCollector.CursorDesktopAuth? auth) : CursorUsageCollector.ICursorDesktopAuthSource
    {
        public CursorUsageCollector.CursorDesktopAuth? TryLoad() => auth;
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

    private sealed class QueueHttpMessageHandler(
        params (Action<HttpRequestMessage> AssertRequest, HttpStatusCode StatusCode, string Content)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(Action<HttpRequestMessage> AssertRequest, HttpStatusCode StatusCode, string Content)> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.IsTrue(_responses.Count > 0, $"Unexpected request to {request.RequestUri}.");
            var (assertRequest, statusCode, content) = _responses.Dequeue();
            assertRequest(request);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previousCulture;
        private readonly CultureInfo _previousUiCulture;
        private readonly CultureInfo? _previousDefaultCulture;
        private readonly CultureInfo? _previousDefaultUiCulture;

        private CultureScope(CultureInfo culture)
        {
            _previousCulture = CultureInfo.CurrentCulture;
            _previousUiCulture = CultureInfo.CurrentUICulture;
            _previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
            _previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        public static CultureScope Use(string cultureName)
        {
            return new CultureScope(CultureInfo.GetCultureInfo(cultureName));
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previousCulture;
            CultureInfo.CurrentUICulture = _previousUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = _previousDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = _previousDefaultUiCulture;
        }
    }
}
