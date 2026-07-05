using System.Net;
using System.Net.Http;
using System.IO;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class CursorUsageCollectorTests
{
    [TestMethod]
    public async Task CollectAsyncParsesCurrentPeriodDashboardUsage()
    {
        var settingsService = CreateSettingsService();
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            (request =>
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                StringAssert.Contains(request.RequestUri?.ToString(), "/auth/full_stripe_profile");
                Assert.IsNotNull(request.Headers.Authorization);
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
                Assert.IsNotNull(request.Headers.Authorization);
            },
            HttpStatusCode.OK,
            """
            {
              "billingCycleEnd": 1783560138855,
              "planUsage": {
                "totalPercentUsed": 42,
                "autoPercentUsed": 12,
                "apiPercentUsed": 65
              },
              "displayMessage": "You've used 42% of your included usage",
              "autoModelSelectedDisplayMessage": "You've used 12% of your included total usage",
              "namedModelSelectedDisplayMessage": "You've used 65% of your included API usage"
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
        Assert.AreEqual("Cursor", usage.Name);
        Assert.AreEqual("Pro", usage.PlanName);
        Assert.AreEqual(3, usage.Windows.Count);
        Assert.AreEqual("Included usage", usage.Windows[0].Title);
        Assert.AreEqual(42, usage.Windows[0].UsedPercent, 0.01);
        Assert.AreEqual("API usage", usage.Windows[2].Title);
        Assert.AreEqual(65, usage.Windows[2].UsedPercent, 0.01);
    }

    [TestMethod]
    public async Task CollectAsyncKeepsZeroPercentDashboardUsage()
    {
        var settingsService = CreateSettingsService();
        using var httpClient = new HttpClient(new QueueHttpMessageHandler(
            (request =>
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                StringAssert.Contains(request.RequestUri?.ToString(), "get-current-period-usage");
                Assert.IsTrue(request.Headers.Contains("Cookie"));
            },
            HttpStatusCode.OK,
            """
            {
              "billingCycleEnd": 1783560138855,
              "planUsage": {
                "totalPercentUsed": 0,
                "autoPercentUsed": 0,
                "apiPercentUsed": 0
              },
              "displayMessage": "You've used 0% of your included usage"
            }
            """)));
        var collector = new CursorUsageCollector(settingsService, httpClient, new StaticCursorDesktopAuthSource(null));

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual("Personal", usage.PlanName);
        Assert.AreEqual(3, usage.Windows.Count);
        Assert.AreEqual(0, usage.Windows[0].UsedPercent, 0.01);
        Assert.AreEqual(100, usage.Windows[0].RemainingPercent, 0.01);
    }

    private static AppSettingsService CreateSettingsService()
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
}
