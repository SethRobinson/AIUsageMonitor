using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using Microsoft.Web.WebView2.Core;

namespace AIUsageMonitor.Views;

public partial class AnthropicApiCreditsSetupWindow : Window
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly AppSettingsService _settingsService;
    private readonly AppLogService _logService;
    private static readonly IReadOnlyList<string> CookieProbeUrls =
    [
        AnthropicApiCreditsCollector.ConsoleBaseUrl,
        AnthropicApiCreditsCollector.LegacyConsoleBaseUrl,
        "https://console.claude.ai",
        "https://claude.com",
        "https://www.claude.com",
        "https://claude.ai",
        "https://www.claude.ai",
        "https://anthropic.com",
        "https://www.anthropic.com"
    ];

    public AnthropicApiCreditsSetupWindow(AppSettingsService settingsService, AppLogService logService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _logService = logService;
        Loaded += WindowOnLoaded;
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WindowOnLoaded;
        StatusTextBlock.Text = "Loading Anthropic Console billing...";

        try
        {
            var settings = _settingsService.Load();
            OrganizationUuidTextBox.Text = settings.AnthropicApiCreditsOrganizationUuid;

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SethsAIUsageMonitor",
                "AnthropicConsoleWebView2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await ConsoleWebView.EnsureCoreWebView2Async(environment);
            ConsoleWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ConsoleWebView.CoreWebView2.NavigationCompleted += ConsoleWebViewOnNavigationCompleted;
            ConsoleWebView.CoreWebView2.Navigate($"{AnthropicApiCreditsCollector.ConsoleBaseUrl}/settings/billing");
            StatusTextBlock.Text = "Sign in if prompted. After billing loads, click Save Login.";
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusTextBlock.Text = $"Could not load WebView2: {ex.Message}";
            _logService.Error(KnownProviders.AnthropicApiCredits, $"Could not load Anthropic Console setup window: {ex.Message}");
        }
    }

    private async void ConsoleWebViewOnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || ConsoleWebView.CoreWebView2 is null)
        {
            return;
        }

        var pageOrgUuid = await TryReadActiveOrganizationFromPageAsync();
        if (IsValidOrganizationUuid(pageOrgUuid) && string.IsNullOrWhiteSpace(OrganizationUuidTextBox.Text))
        {
            OrganizationUuidTextBox.Text = pageOrgUuid;
        }
    }

    private async void SaveLoginButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (ConsoleWebView.CoreWebView2 is null)
        {
            StatusTextBlock.Text = "Anthropic Console is not ready yet.";
            return;
        }

        try
        {
            StatusTextBlock.Text = "Reading Anthropic Console cookies...";
            var cookies = await GetAnthropicCookiesAsync();
            var cookieHeader = BuildCookieHeader(cookies);
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                var currentHost = TryGetCurrentPageHost();
                StatusTextBlock.Text = string.IsNullOrWhiteSpace(currentHost)
                    ? "No Anthropic Console cookies were found. Make sure you are signed in first."
                    : $"No Anthropic Console cookies were found for the current page ({currentHost}). Make sure billing is fully loaded, then try Save Login again.";
                return;
            }

            StatusTextBlock.Text = "Finding Anthropic organization...";
            var manualOrgUuid = OrganizationUuidTextBox.Text.Trim();
            var pageOrgUuid = await TryReadActiveOrganizationFromPageAsync();
            var cookieOrgUuid = TryReadOrganizationUuidFromCookies(cookies);
            var organizations = await TryDiscoverOrganizationsFromBootstrapAsync(cookieHeader);
            var candidates = BuildOrganizationCandidates(manualOrgUuid, pageOrgUuid, cookieOrgUuid, organizations);

            if (candidates.Count == 0)
            {
                StatusTextBlock.Text = "Could not find an Anthropic organization automatically. Paste the org UUID shown by Console, then click Save Login again.";
                return;
            }

            AnthropicApiCreditsSnapshot? verifiedSnapshot = null;
            AnthropicConsoleOrganization verifiedOrganization = default;
            var hasVerifiedOrganization = false;
            var lastFailure = string.Empty;

            foreach (var candidate in candidates)
            {
                try
                {
                    StatusTextBlock.Text = $"Verifying Anthropic billing balance for {FormatOrganization(candidate)}...";
                    verifiedSnapshot = await TryFetchSnapshotInWebViewAsync(candidate) ??
                                       await AnthropicApiCreditsCollector.FetchSnapshotAsync(
                                           HttpClient,
                                           cookieHeader,
                                           candidate.Uuid,
                                           candidate.Name,
                                           CancellationToken.None);
                    verifiedOrganization = candidate;
                    hasVerifiedOrganization = true;
                    break;
                }
                catch (AnthropicApiCreditsException ex) when (ex.Kind is AnthropicApiCreditsFailureKind.NotFound or AnthropicApiCreditsFailureKind.Schema or AnthropicApiCreditsFailureKind.Transient)
                {
                    lastFailure = ex.Message;
                }
            }

            if (verifiedSnapshot is null && IsValidOrganizationUuid(manualOrgUuid))
            {
                var pageSnapshot = await TryReadVisibleBalanceSnapshotAsync(manualOrgUuid, string.Empty);
                if (pageSnapshot is not null)
                {
                    verifiedSnapshot = pageSnapshot;
                    verifiedOrganization = new AnthropicConsoleOrganization(manualOrgUuid, string.Empty);
                    hasVerifiedOrganization = true;
                }
            }

            if (verifiedSnapshot is null || !hasVerifiedOrganization)
            {
                StatusTextBlock.Text = string.IsNullOrWhiteSpace(lastFailure)
                    ? "Could not verify Anthropic billing balance from the API or visible billing page."
                    : $"Could not verify Anthropic billing balance from the API or visible billing page: {lastFailure}";
                return;
            }

            var settings = _settingsService.Load();
            settings.AnthropicApiCreditsCookieHeaderProtected = ProtectedStringService.Protect(cookieHeader);
            settings.AnthropicApiCreditsCookiesCapturedAt = DateTimeOffset.Now;
            settings.AnthropicApiCreditsOrganizationUuid = verifiedOrganization.Uuid;
            settings.AnthropicApiCreditsOrganizationName = verifiedOrganization.Name;
            settings.AnthropicApiCreditsLastBalance = verifiedSnapshot.ToCache();
            settings.SetProviderEnabled(KnownProviders.AnthropicApiCredits, true);
            _settingsService.Save(settings);

            StatusTextBlock.Text = $"Anthropic API Credits login saved for {FormatOrganization(verifiedOrganization)}. You can close this window and refresh.";
            _logService.Info(KnownProviders.AnthropicApiCredits, "Anthropic Console billing cookies saved for API credits balance.");
            DialogResult = true;
        }
        catch (AnthropicApiCreditsException ex) when (ex.Kind is AnthropicApiCreditsFailureKind.AuthExpired)
        {
            StatusTextBlock.Text = "Anthropic Console login was rejected. Sign in to Console billing, then click Save Login again.";
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or JsonException or TaskCanceledException)
        {
            StatusTextBlock.Text = $"Could not save Anthropic API Credits login: {ex.Message}";
            _logService.Error(KnownProviders.AnthropicApiCredits, $"Could not save Anthropic Console billing cookies: {ex.Message}");
        }
    }

    private async Task<AnthropicApiCreditsSnapshot?> TryFetchSnapshotInWebViewAsync(AnthropicConsoleOrganization organization)
    {
        if (ConsoleWebView.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            var creditsPath = $"/api/organizations/{organization.Uuid}/prepaid/credits";
            var expiryPath = $"/api/organizations/{organization.Uuid}/prepaid/credit_expiry";
            var result = await ConsoleWebView.CoreWebView2.ExecuteScriptAsync(
                $$"""
                (async () => {
                  const read = async (path) => {
                    try {
                      const response = await fetch(path, {
                        method: 'GET',
                        credentials: 'include',
                        headers: { 'Accept': 'application/json, text/plain, */*' }
                      });
                      return {
                        ok: response.ok,
                        status: response.status,
                        contentType: response.headers.get('content-type') || '',
                        text: await response.text()
                      };
                    } catch (error) {
                      return { ok: false, status: 0, contentType: '', text: String(error && error.message ? error.message : error) };
                    }
                  };
                  return {
                    credits: await read({{JsonSerializer.Serialize(creditsPath)}}),
                    expiry: await read({{JsonSerializer.Serialize(expiryPath)}})
                  };
                })()
                """);
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("credits", out var credits) ||
                credits.ValueKind != JsonValueKind.Object ||
                !credits.TryGetProperty("ok", out var okElement) ||
                okElement.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            var creditsText = TryGetString(credits, "text");
            if (string.IsNullOrWhiteSpace(creditsText))
            {
                return null;
            }

            var expiryText = root.TryGetProperty("expiry", out var expiry) &&
                             expiry.ValueKind == JsonValueKind.Object &&
                             expiry.TryGetProperty("ok", out var expiryOk) &&
                             expiryOk.ValueKind == JsonValueKind.True
                ? TryGetString(expiry, "text")
                : null;

            return AnthropicApiCreditsCollector.ParseSnapshot(
                creditsText,
                expiryText,
                organization.Uuid,
                organization.Name,
                DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or AnthropicApiCreditsException)
        {
            return null;
        }
    }

    private async Task<AnthropicApiCreditsSnapshot?> TryReadVisibleBalanceSnapshotAsync(
        string organizationUuid,
        string organizationName)
    {
        if (ConsoleWebView.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            var result = await ConsoleWebView.CoreWebView2.ExecuteScriptAsync(
                """
                (() => document.body ? document.body.innerText : '')()
                """);
            using var document = JsonDocument.Parse(result);
            var text = document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString() ?? string.Empty
                : string.Empty;

            return TryParseVisibleBalanceSnapshot(text, organizationUuid, organizationName, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    internal static AnthropicApiCreditsSnapshot? TryParseVisibleBalanceSnapshot(
        string pageText,
        string organizationUuid,
        string organizationName,
        DateTimeOffset verifiedAt)
    {
        if (string.IsNullOrWhiteSpace(pageText) ||
            !pageText.Contains("Credit balance", StringComparison.OrdinalIgnoreCase) ||
            !pageText.Contains("Remaining balance", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var remainingIndex = pageText.IndexOf("Remaining balance", StringComparison.OrdinalIgnoreCase);
        var creditIndex = pageText.IndexOf("Credit balance", StringComparison.OrdinalIgnoreCase);
        var searchStart = creditIndex >= 0 ? creditIndex : 0;
        var searchLength = remainingIndex > searchStart
            ? remainingIndex - searchStart
            : Math.Min(pageText.Length - searchStart, 800);
        var searchText = pageText.Substring(searchStart, searchLength);
        var match = UsdAmountRegex().Match(searchText);
        if (!match.Success)
        {
            match = UsdAmountRegex().Match(pageText);
        }

        if (!match.Success ||
            !decimal.TryParse(match.Groups["amount"].Value.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var dollars) ||
            dollars < 0 ||
            dollars > AnthropicApiCreditsCollector.MaxSupportedCents / 100m)
        {
            return null;
        }

        var cents = decimal.Round(dollars * 100m, 0, MidpointRounding.AwayFromZero);
        return new AnthropicApiCreditsSnapshot(
            cents,
            null,
            null,
            null,
            verifiedAt,
            organizationUuid,
            organizationName);
    }

    private void ClearLoginButtonOnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _settingsService.Load();
            settings.AnthropicApiCreditsCookieHeaderProtected = string.Empty;
            settings.AnthropicApiCreditsCookiesCapturedAt = null;
            settings.AnthropicApiCreditsOrganizationUuid = string.Empty;
            settings.AnthropicApiCreditsOrganizationName = string.Empty;
            settings.AnthropicApiCreditsLastBalance = null;
            _settingsService.Save(settings);
            OrganizationUuidTextBox.Text = string.Empty;
            StatusTextBlock.Text = "Anthropic API Credits login cleared.";
            _logService.Info(KnownProviders.AnthropicApiCredits, "Anthropic Console billing login cleared.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusTextBlock.Text = $"Could not clear Anthropic API Credits login: {ex.Message}";
        }
    }

    private void CloseButtonOnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private async Task<string> TryReadActiveOrganizationFromPageAsync()
    {
        if (ConsoleWebView.CoreWebView2 is null)
        {
            return string.Empty;
        }

        try
        {
            var result = await ConsoleWebView.CoreWebView2.ExecuteScriptAsync(
                """
                (() => {
                  const values = {};
                  for (const raw of document.cookie.split(';')) {
                    const part = raw.trim();
                    const index = part.indexOf('=');
                    if (index > 0) values[part.slice(0, index)] = decodeURIComponent(part.slice(index + 1));
                  }
                  return {
                    lastActiveOrg: values.lastActiveOrg || values.trustPortalLastActiveOrg || '',
                    href: location.href
                  };
                })()
                """);
            using var document = JsonDocument.Parse(result);
            return TryGetString(document.RootElement, "lastActiveOrg");
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return string.Empty;
        }
    }

    private async Task<IReadOnlyList<CoreWebView2Cookie>> GetAnthropicCookiesAsync()
    {
        if (ConsoleWebView.CoreWebView2 is null)
        {
            return [];
        }

        var urls = new List<string>();
        var currentOrigin = TryGetCurrentPageOrigin();
        if (!string.IsNullOrWhiteSpace(currentOrigin))
        {
            urls.Add(currentOrigin);
        }

        urls.AddRange(CookieProbeUrls);

        var cookies = new List<CoreWebView2Cookie>();
        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                cookies.AddRange(await ConsoleWebView.CoreWebView2.CookieManager.GetCookiesAsync(url));
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return cookies
            .GroupBy(cookie => $"{cookie.Domain}\n{cookie.Path}\n{cookie.Name}\n{cookie.Value}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string BuildCookieHeader(IEnumerable<CoreWebView2Cookie> cookies)
    {
        return string.Join("; ",
            cookies
                .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name) &&
                                 !string.IsNullOrWhiteSpace(cookie.Value) &&
                                 IsSupportedCookieDomain(cookie.Domain))
                .GroupBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(cookie => CookieDomainPriority(cookie.Domain))
                    .First())
                .Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    private static string TryReadOrganizationUuidFromCookies(IEnumerable<CoreWebView2Cookie> cookies)
    {
        return cookies
            .Where(cookie => string.Equals(cookie.Name, "lastActiveOrg", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(cookie.Name, "trustPortalLastActiveOrg", StringComparison.OrdinalIgnoreCase))
            .Select(cookie => cookie.Value)
            .FirstOrDefault(IsValidOrganizationUuid) ?? string.Empty;
    }

    private static async Task<IReadOnlyList<AnthropicConsoleOrganization>> TryDiscoverOrganizationsFromBootstrapAsync(string cookieHeader)
    {
        try
        {
            using var request = AnthropicApiCreditsCollector.BuildConsoleRequest(
                HttpMethod.Get,
                $"{AnthropicApiCreditsCollector.ConsoleBaseUrl}/api/bootstrap?statsig_hashing_algorithm=djb2&growthbook_format=sdk&include_system_prompts=false",
                cookieHeader);
            using var response = await HttpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json) || json.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                return [];
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("account", out var account) ||
                !account.TryGetProperty("memberships", out var memberships) ||
                memberships.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var organizations = new List<AnthropicConsoleOrganization>();
            foreach (var membership in memberships.EnumerateArray())
            {
                if (!membership.TryGetProperty("organization", out var organization))
                {
                    continue;
                }

                var uuid = TryGetString(organization, "uuid");
                if (!IsValidOrganizationUuid(uuid) || !HasApiCapability(organization))
                {
                    continue;
                }

                organizations.Add(new AnthropicConsoleOrganization(uuid, TryGetString(organization, "name")));
            }

            return organizations
                .GroupBy(organization => organization.Uuid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static List<AnthropicConsoleOrganization> BuildOrganizationCandidates(
        string manualOrgUuid,
        string pageOrgUuid,
        string cookieOrgUuid,
        IReadOnlyList<AnthropicConsoleOrganization> organizations)
    {
        var candidates = new List<AnthropicConsoleOrganization>();

        AddCandidate(candidates, organizations, manualOrgUuid);
        AddCandidate(candidates, organizations, pageOrgUuid);
        AddCandidate(candidates, organizations, cookieOrgUuid);

        if (candidates.Count == 0 && organizations.Count == 1)
        {
            candidates.Add(organizations[0]);
        }

        return candidates;
    }

    private static void AddCandidate(
        List<AnthropicConsoleOrganization> candidates,
        IReadOnlyList<AnthropicConsoleOrganization> organizations,
        string uuid)
    {
        if (!IsValidOrganizationUuid(uuid) ||
            candidates.Any(candidate => string.Equals(candidate.Uuid, uuid, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var knownOrganization = organizations.FirstOrDefault(organization =>
            string.Equals(organization.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
        candidates.Add(string.IsNullOrWhiteSpace(knownOrganization.Uuid)
            ? new AnthropicConsoleOrganization(uuid, string.Empty)
            : knownOrganization);
    }

    private static bool HasApiCapability(JsonElement organization)
    {
        if (!organization.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return capabilities.EnumerateArray().Any(capability =>
            capability.ValueKind == JsonValueKind.String &&
            string.Equals(capability.GetString(), "api", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidOrganizationUuid(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value.Trim(), out _);
    }

    private string TryGetCurrentPageOrigin()
    {
        if (ConsoleWebView.CoreWebView2 is null ||
            !Uri.TryCreate(ConsoleWebView.CoreWebView2.Source, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private string TryGetCurrentPageHost()
    {
        if (ConsoleWebView.CoreWebView2 is null ||
            !Uri.TryCreate(ConsoleWebView.CoreWebView2.Source, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        return uri.Host;
    }

    private static bool IsSupportedCookieDomain(string domain)
    {
        var normalizedDomain = domain.Trim().TrimStart('.').ToLowerInvariant();
        return HostMatches(normalizedDomain, "platform.claude.com") ||
               HostMatches(normalizedDomain, "claude.com") ||
               HostMatches(normalizedDomain, "anthropic.com") ||
               HostMatches(normalizedDomain, "claude.ai");
    }

    private static int CookieDomainPriority(string domain)
    {
        var normalizedDomain = domain.Trim().TrimStart('.').ToLowerInvariant();
        if (HostMatches(normalizedDomain, "platform.claude.com"))
        {
            return 400;
        }

        if (HostMatches(normalizedDomain, "claude.com"))
        {
            return 300;
        }

        if (HostMatches(normalizedDomain, "anthropic.com"))
        {
            return 200;
        }

        if (HostMatches(normalizedDomain, "claude.ai"))
        {
            return 100;
        }

        return 0;
    }

    private static bool HostMatches(string host, string suffix)
    {
        return string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string FormatOrganization(AnthropicConsoleOrganization organization)
    {
        return string.IsNullOrWhiteSpace(organization.Name)
            ? organization.Uuid
            : string.Create(CultureInfo.InvariantCulture, $"{organization.Name} ({organization.Uuid})");
    }

    private readonly record struct AnthropicConsoleOrganization(string Uuid, string Name);

    [GeneratedRegex(@"\$(?<amount>\d{1,3}(?:,\d{3})*(?:\.\d{2})?|\d+(?:\.\d{2})?)")]
    private static partial Regex UsdAmountRegex();
}
