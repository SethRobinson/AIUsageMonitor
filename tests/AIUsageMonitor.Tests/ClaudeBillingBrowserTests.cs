using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Services;
using Microsoft.Web.WebView2.Core;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ClaudeBillingBrowserTests
{
    private const string CreditsPath = "/api/organizations/ab9cc8d1-8d50-4f96-805d-7c22f52bcae2/prepaid/credits";
    private string _root = null!;
    private ClaudeBillingService _service = null!;
    private readonly List<string> _profiles = [];

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        _service = new ClaudeBillingService(new AppSettingsService(_root), profileRoot: Path.Combine(_root, "profiles"));
    }

    private string NewProfile()
    {
        var id = Guid.NewGuid().ToString("N");
        _profiles.Add(id);
        return _service.GetProfileDirectory(id);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var id in _profiles) await _service.DeleteProfileAsync(id);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task BrowserAwaitsFetchAndRetainsItsOwnSessionAcrossRefreshes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var site = new FakeSite { SetLogin = true };
        var browser = new ClaudeBillingBrowser(site.Configure);
        var profile = NewProfile();
        await using (var session = await browser.OpenAsync(profile, timeout.Token))
        {
            var result = await session.GetAsync(CreditsPath, timeout.Token);
            Assert.AreEqual(200, result.Status);
            using var json = JsonDocument.Parse(result.Content);
            Assert.AreEqual("$113.85 credits", ClaudeBillingService.ParseBalance(json.RootElement).RemainingText);
        }
        site.SetLogin = false;
        await using (var session = await browser.OpenAsync(profile, timeout.Token))
            Assert.AreEqual(200, (await session.GetAsync("/api/account", timeout.Token)).Status,
                "A later refresh must reuse the signed-in browser profile.");
        await using (var other = await browser.OpenAsync(NewProfile(), timeout.Token))
            Assert.AreEqual(401, (await other.GetAsync("/api/account", timeout.Token)).Status,
                "Another account's browser profile must not inherit this login.");
        Assert.IsTrue(site.Requests.All(r => r.Method == "GET"));
        Assert.AreEqual(3, site.Requests.Count(r => r.Path.StartsWith("/api/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DisconnectClosesBrowserCancelsReadAndPreventsStaleProfileRecreation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var site = new FakeSite { SetLogin = true, FetchDelay = 5000 };
        var browser = new ClaudeBillingBrowser(site.Configure);
        var profile = NewProfile();
        await using var session = await browser.OpenAsync(profile, timeout.Token);
        var pending = session.GetAsync(CreditsPath, timeout.Token);
        await _service.DeleteProfileAsync(_profiles.Single()).WaitAsync(timeout.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.IsFalse(Directory.Exists(profile));
        Assert.ThrowsExactly<OperationCanceledException>(() => browser.OpenAsync(profile, timeout.Token));
    }

    [TestMethod]
    public async Task BrowserReadHonorsCancellation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var browser = new ClaudeBillingBrowser(new FakeSite { SetLogin = true, FetchDelay = 5000 }.Configure);
        await using var session = await browser.OpenAsync(NewProfile(), timeout.Token);
        using var cancel = new CancellationTokenSource();
        var pending = session.GetAsync(CreditsPath, cancel.Token);
        cancel.CancelAfter(50);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    [TestMethod]
    [DataRow("https://example.com/api/account")]
    [DataRow("//example.com/api/account")]
    [DataRow("/api/account?redirect=example.com")]
    [DataRow("/api/organizations/../../account/prepaid/credits")]
    [DataRow("/api/organizations/ab9cc8d1-8d50-4f96-805d-7c22f52bcae2/prepaid/purchase")]
    public void BrowserTransportRejectsUnrelatedRoutes(string path) =>
        Assert.ThrowsExactly<ArgumentException>(() => ClaudeBillingBrowser.ValidateReadPath(path));

    // Real Chromium networking/Promise handling with every website request intercepted locally.
    // No user settings, cookies, provider APIs, desktop capture, or visible windows are involved.
    private sealed class FakeSite
    {
        public bool SetLogin { get; set; }
        public int FetchDelay { get; set; } = 40;
        public ConcurrentQueue<(string Method, string Path)> Requests { get; } = new();

        public void Configure(CoreWebView2 core, CoreWebView2Environment environment)
        {
            Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            if (SetLogin)
            {
                var cookie = core.CookieManager.CreateCookie("sessionKey", "fake-billing-session", ".claude.ai", "/");
                cookie.IsHttpOnly = true;
                cookie.IsSecure = true;
                cookie.Expires = DateTime.Now.AddDays(1);
                core.CookieManager.AddOrUpdateCookie(cookie);
            }
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                var uri = new Uri(e.Request.Uri);
                Requests.Enqueue((e.Request.Method, uri.AbsolutePath));
                var isPage = uri.Host == "claude.ai" && uri.AbsolutePath == "/settings/billing";
                var loggedIn = e.Request.Headers.Contains("Cookie") &&
                    e.Request.Headers.GetHeader("Cookie").Contains("sessionKey=fake-billing-session", StringComparison.Ordinal);
                var status = isPage || loggedIn ? 200 : 401;
                var body = isPage ? $$"""
                    <!doctype html><title>Fake billing</title><script>
                    const originalFetch = fetch;
                    window.fetch = async (...args) => {
                        await new Promise(resolve => setTimeout(resolve, {{FetchDelay}}));
                        return originalFetch(...args);
                    };
                    </script>
                    """ : loggedIn ? """{"amount":11385,"currency":"USD","uuid":"fake-account"}""" : "{}";
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), status,
                    status == 200 ? "OK" : "Unauthorized", "Content-Type: " + (isPage ? "text/html" : "application/json"));
            };
        }
    }
}
