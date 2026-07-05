using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using Microsoft.Data.Sqlite;

namespace AIUsageMonitor.Collectors;

public sealed class CursorUsageCollector : IUsageCollector
{
    private readonly AppSettingsService _settingsService;
    private readonly HttpClient _httpClient;
    private readonly ICursorDesktopAuthSource _desktopAuthSource;

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public CursorUsageCollector(AppSettingsService settingsService, HttpClient? httpClient = null)
        : this(settingsService, httpClient, CursorDesktopStateAuthSource.Instance)
    {
    }

    internal CursorUsageCollector(
        AppSettingsService settingsService,
        HttpClient? httpClient,
        ICursorDesktopAuthSource desktopAuthSource)
    {
        _settingsService = settingsService;
        _httpClient = httpClient ?? SharedHttpClient;
        _desktopAuthSource = desktopAuthSource;
    }

    public string ProviderName => KnownProviders.Cursor;

    public async Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.Load();

        return string.Equals(settings.CursorUsageMode, AppSettings.CursorUsageModeTeamsApiKey, StringComparison.Ordinal)
            ? await CollectTeamsApiUsageAsync(settings, cancellationToken)
            : await CollectPersonalDashboardUsageAsync(settings, cancellationToken);
    }

    private async Task<ProviderUsage> CollectPersonalDashboardUsageAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var desktopUsage = await TryCollectDesktopUsageAsync(cancellationToken);
        if (desktopUsage is not null)
        {
            return desktopUsage;
        }

        var dashboardUsage = await TryCollectDashboardUsageAsync(settings, cancellationToken);
        if (dashboardUsage is not null)
        {
            return dashboardUsage;
        }

        return ProviderUsageFactory.Unavailable(
            "Cursor",
            "Cursor personal dashboard login is not saved. Open Settings, then use Cursor Setup.",
            "Cursor dashboard not configured");
    }

    private async Task<ProviderUsage> CollectTeamsApiUsageAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var apiKey = string.IsNullOrWhiteSpace(settings.CursorApiKey)
            ? Environment.GetEnvironmentVariable("CURSOR_API_KEY")
            : settings.CursorApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderUsageFactory.Unavailable(
                ProviderName,
                "Cursor Teams Admin API key is not configured. Open Settings, then use Cursor Setup.",
                "Cursor Admin API not configured");
        }

        if (apiKey.StartsWith("crsr_", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderUsageFactory.Unavailable(
                ProviderName,
                "The saved crsr_ key is not accepted by Cursor's Teams Admin API. Choose Personal Subscription in Cursor Setup instead.",
                "Cursor personal plan");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.cursor.com/teams/spend");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:")));
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var spendCents = 0;

        if (root.TryGetProperty("teamMemberSpend", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in members.EnumerateArray())
            {
                if (member.TryGetProperty("spendCents", out var spendElement))
                {
                    spendCents += spendElement.GetInt32();
                }
            }
        }

        var monthlyBudgetDollars = settings.CursorIncludedBudgetDollars;
        if (string.IsNullOrWhiteSpace(settings.CursorApiKey) &&
            ProviderJson.TryParseDouble(Environment.GetEnvironmentVariable("CURSOR_INCLUDED_BUDGET_DOLLARS"), out var parsedBudget))
        {
            monthlyBudgetDollars = parsedBudget;
        }

        var spendDollars = spendCents / 100d;
        var usedPercent = monthlyBudgetDollars <= 0 ? 0 : Math.Clamp(spendDollars * 100d / monthlyBudgetDollars, 0, 100);

        return new ProviderUsage
        {
            Name = ProviderName,
            PlanName = "Teams",
            Source = "Cursor Admin API",
            StatusMessage = $"Current-cycle spend ${spendDollars:0.00} of configured ${monthlyBudgetDollars:0.00} budget.",
            Windows =
            [
                ProviderUsageFactory.PercentWindow("Monthly", usedPercent, TryParseSubscriptionCycle(root))
            ]
        };
    }

    private async Task<ProviderUsage?> TryCollectDashboardUsageAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        string cookieHeader;

        try
        {
            cookieHeader = ProtectedStringService.Unprotect(settings.CursorDashboardCookieHeaderProtected);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return ProviderUsageFactory.Unavailable(
                "Cursor",
                $"Saved Cursor dashboard login could not be decrypted: {ex.Message}. Re-save it from Cursor Dashboard Login.",
                "Cursor dashboard cookies");
        }

        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        ProviderUsage? zeroLimitUsage = null;

        using (var currentPeriodRequest = BuildDashboardRequest(
                   HttpMethod.Get,
                   "https://cursor.com/api/dashboard/get-current-period-usage",
                   cookieHeader))
        using (var currentPeriodResponse = await _httpClient.SendAsync(currentPeriodRequest, cancellationToken))
        {
            var authFailure = BuildAuthFailureUsage(currentPeriodResponse);
            if (authFailure is not null)
            {
                return authFailure;
            }

            if (currentPeriodResponse.IsSuccessStatusCode)
            {
                await using var currentPeriodStream = await currentPeriodResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var currentPeriodDocument = await JsonDocument.ParseAsync(currentPeriodStream, cancellationToken: cancellationToken);
                var currentPeriodUsage = ParseCurrentPeriodUsage(currentPeriodDocument.RootElement);
                if (currentPeriodUsage is not null)
                {
                    return currentPeriodUsage;
                }

                zeroLimitUsage = currentPeriodUsage;
            }
        }

        using var request = BuildDashboardRequest(HttpMethod.Get, "https://cursor.com/api/usage-summary", cookieHeader);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        var dashboardAuthFailure = BuildAuthFailureUsage(response);
        if (dashboardAuthFailure is not null)
        {
            return dashboardAuthFailure;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var dashboardUsage = ParseDashboardUsage(document.RootElement);
        if (dashboardUsage.IsUnavailable)
        {
            return dashboardUsage.StatusMessage.Contains("Free or zero-limit", StringComparison.OrdinalIgnoreCase)
                ? dashboardUsage
                : zeroLimitUsage ?? dashboardUsage;
        }

        return dashboardUsage.Windows.Count > 0
            ? dashboardUsage
            : zeroLimitUsage;
    }

    private async Task<ProviderUsage?> TryCollectDesktopUsageAsync(CancellationToken cancellationToken)
    {
        var auth = _desktopAuthSource.TryLoad();
        if (auth is null)
        {
            return null;
        }

        var profile = await TryCollectDesktopProfileAsync(auth, cancellationToken);
        using var request = BuildDesktopRequest(
            HttpMethod.Post,
            new Uri(new Uri(auth.BackendUrl), "/aiserver.v1.DashboardService/GetCurrentPeriodUsage"),
            auth.AccessToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ParseCurrentPeriodUsage(
                document.RootElement,
                "Cursor desktop app",
                BuildDesktopPlanName(profile, auth),
                BuildDesktopStatusMessage(profile, auth));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<CursorDesktopProfile?> TryCollectDesktopProfileAsync(
        CursorDesktopAuth auth,
        CancellationToken cancellationToken)
    {
        using var request = BuildDesktopRequest(
            HttpMethod.Get,
            new Uri(new Uri(auth.BackendUrl), "/auth/full_stripe_profile"),
            auth.AccessToken);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            return new CursorDesktopProfile(
                TryGetString(root, "membershipType"),
                TryGetString(root, "subscriptionStatus"),
                TryGetBool(root, "isOnBillableAuto"));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private static HttpRequestMessage BuildDesktopRequest(HttpMethod method, Uri uri, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("User-Agent", "AIUsageMonitor/1.0");
        request.Headers.TryAddWithoutValidation("x-cursor-client-version", "1.2.2");
        return request;
    }

    private static ProviderUsage? BuildAuthFailureUsage(HttpResponseMessage response)
    {
        return response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
            ? ProviderUsageFactory.Unavailable(
                "Cursor",
                "Saved Cursor dashboard login was rejected. Open Settings, then use Cursor Setup again.",
                "Cursor dashboard")
            : null;
    }

    private static HttpRequestMessage BuildDashboardRequest(HttpMethod method, string url, string cookieHeader)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.Referrer = new Uri("https://cursor.com/dashboard/usage");
        return request;
    }

    private static ProviderUsage ParseDashboardUsage(JsonElement root)
    {
        var billingCycleEnd = TryGetDateTimeOffset(root, "billingCycleEnd");
        var membershipType = root.TryGetProperty("membershipType", out var membershipElement)
            ? membershipElement.GetString()
            : null;
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("individualUsage", out var individualUsage) &&
            individualUsage.TryGetProperty("plan", out var plan) &&
            TryGetDouble(plan, "limit", out var limitCents) &&
            limitCents > 0 &&
            TryGetDouble(plan, "used", out var usedCents))
        {
            var remainingCents = TryGetDouble(plan, "remaining", out var parsedRemaining)
                ? parsedRemaining
                : Math.Max(limitCents - usedCents, 0);
            var usedPercent = Math.Clamp(usedCents * 100d / limitCents, 0, 100);
            windows.Add(ProviderUsageFactory.PercentWindow(
                "Monthly included",
                usedPercent,
                billingCycleEnd,
                $"${usedCents / 100d:0.00} used of ${limitCents / 100d:0.00}; ${remainingCents / 100d:0.00} left"));
        }

        if (root.TryGetProperty("individualUsage", out individualUsage) &&
            individualUsage.TryGetProperty("onDemand", out var onDemand) &&
            onDemand.TryGetProperty("enabled", out var enabledElement) &&
            enabledElement.ValueKind == JsonValueKind.True &&
            TryGetDouble(onDemand, "limit", out var onDemandLimitCents) &&
            onDemandLimitCents > 0 &&
            TryGetDouble(onDemand, "used", out var onDemandUsedCents))
        {
            var remainingCents = TryGetDouble(onDemand, "remaining", out var parsedRemaining)
                ? parsedRemaining
                : Math.Max(onDemandLimitCents - onDemandUsedCents, 0);
            var usedPercent = Math.Clamp(onDemandUsedCents * 100d / onDemandLimitCents, 0, 100);
            windows.Add(ProviderUsageFactory.PercentWindow(
                "On-demand",
                usedPercent,
                billingCycleEnd,
                $"${onDemandUsedCents / 100d:0.00} used of ${onDemandLimitCents / 100d:0.00}; ${remainingCents / 100d:0.00} left"));
        }

        if (windows.Count == 0 && LooksLikeFreeOrZeroLimitDashboard(root))
        {
            return ProviderUsageFactory.Unavailable(
                "Cursor",
                "Cursor dashboard login works, but Cursor reports a Free or zero-limit profile for this browser session. Re-save Cursor Setup while signed into the paid profile you use in Cursor.",
                "Cursor dashboard",
                PlanNameFormatter.Format(membershipType));
        }

        if (windows.Count == 0)
        {
            return ProviderUsageFactory.Unavailable(
                "Cursor",
                "Cursor dashboard response did not include individual plan usage fields.",
                "Cursor dashboard");
        }

        var planName = PlanNameFormatter.Format(membershipType);

        return new ProviderUsage
        {
            Name = "Cursor",
            PlanName = planName,
            Source = "Cursor dashboard",
            StatusMessage = string.IsNullOrWhiteSpace(planName)
                ? "Personal Cursor usage from dashboard."
                : $"Cursor {planName} usage from dashboard.",
            Windows = windows
        };
    }

    private static ProviderUsage? ParseCurrentPeriodUsage(
        JsonElement root,
        string source = "Cursor dashboard",
        string planName = "Personal",
        string statusMessage = "Personal Cursor usage from dashboard current-period API.")
    {
        if (!TryGetObject(root, "planUsage", out var planUsage))
        {
            return null;
        }

        var billingCycleEnd = TryGetUnixMilliseconds(root, "billingCycleEnd");
        var windows = new List<UsageWindow>();
        AddPercentWindow(
            windows,
            planUsage,
            "totalPercentUsed",
            "Included usage",
            billingCycleEnd,
            TryGetString(root, "displayMessage"));
        AddPercentWindow(
            windows,
            planUsage,
            "autoPercentUsed",
            "Auto models",
            billingCycleEnd,
            TryGetString(root, "autoModelSelectedDisplayMessage"));
        AddPercentWindow(
            windows,
            planUsage,
            "apiPercentUsed",
            "API usage",
            billingCycleEnd,
            TryGetString(root, "namedModelSelectedDisplayMessage"));

        if (windows.Count == 0)
        {
            return null;
        }

        return new ProviderUsage
        {
            Name = "Cursor",
            PlanName = planName,
            Source = source,
            StatusMessage = statusMessage,
            Windows = windows
        };
    }

    private static void AddPercentWindow(
        ICollection<UsageWindow> windows,
        JsonElement element,
        string propertyName,
        string title,
        DateTimeOffset? resetAt,
        string? detail)
    {
        if (!TryGetDouble(element, propertyName, out var usedPercent) ||
            usedPercent < 0 ||
            usedPercent > 100)
        {
            return;
        }

        windows.Add(ProviderUsageFactory.PercentWindow(
            title,
            usedPercent,
            resetAt,
            string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim()));
    }

    private static bool LooksLikeFreeOrZeroLimitDashboard(JsonElement root)
    {
        var membershipType = TryGetString(root, "membershipType");
        if (!root.TryGetProperty("individualUsage", out var individualUsage) ||
            !TryGetObject(individualUsage, "plan", out var plan))
        {
            return false;
        }

        var hasLimit = TryGetDouble(plan, "limit", out var limit);
        var hasRemaining = TryGetDouble(plan, "remaining", out var remaining);
        var hasUsed = TryGetDouble(plan, "used", out var used);
        var isFree = string.Equals(membershipType, "free", StringComparison.OrdinalIgnoreCase);

        return isFree && hasLimit && limit <= 0 && (!hasUsed || used <= 0) && (!hasRemaining || remaining <= 0);
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        return ProviderJson.TryGetDouble(element, propertyName, out value);
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement property)
    {
        return element.TryGetProperty(propertyName, out property) &&
            property.ValueKind == JsonValueKind.Object;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return ProviderJson.TryGetString(element, propertyName);
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
    }

    private static string BuildDesktopPlanName(CursorDesktopProfile? profile, CursorDesktopAuth auth)
    {
        var membershipType = profile?.MembershipType ?? auth.MembershipType;
        var planName = PlanNameFormatter.Format(membershipType);
        return string.Equals(planName, "Free", StringComparison.OrdinalIgnoreCase) &&
               profile?.IsOnBillableAuto == true
            ? "Billable Auto"
            : string.IsNullOrWhiteSpace(planName) ? "Personal" : planName;
    }

    private static string BuildDesktopStatusMessage(CursorDesktopProfile? profile, CursorDesktopAuth auth)
    {
        var subscriptionStatus = profile?.SubscriptionStatus ?? auth.SubscriptionStatus;
        if (profile?.IsOnBillableAuto == true &&
            !string.IsNullOrWhiteSpace(subscriptionStatus))
        {
            return $"Personal Cursor usage from desktop app. Desktop profile reports billable auto with {subscriptionStatus} subscription status.";
        }

        return "Personal Cursor usage from desktop app current-period API.";
    }

    private static DateTimeOffset? TryGetUnixMilliseconds(JsonElement element, string propertyName)
    {
        return ProviderJson.TryGetUnixMilliseconds(element, propertyName);
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        return ProviderJson.TryGetDateTimeOffset(element, propertyName);
    }

    private static DateTimeOffset? TryParseSubscriptionCycle(JsonElement root)
    {
        return ProviderJson.TryGetUnixMilliseconds(root, "subscriptionCycleStart")?.AddMonths(1);
    }

    internal interface ICursorDesktopAuthSource
    {
        CursorDesktopAuth? TryLoad();
    }

    internal sealed record CursorDesktopAuth(
        string AccessToken,
        string BackendUrl,
        string? MembershipType,
        string? SubscriptionStatus);

    private sealed record CursorDesktopProfile(
        string? MembershipType,
        string? SubscriptionStatus,
        bool? IsOnBillableAuto);

    private sealed class CursorDesktopStateAuthSource : ICursorDesktopAuthSource
    {
        public static readonly CursorDesktopStateAuthSource Instance = new();

        private const string AccessTokenKey = "cursorAuth/accessToken";
        private const string MembershipTypeKey = "cursorAuth/stripeMembershipType";
        private const string SubscriptionStatusKey = "cursorAuth/stripeSubscriptionStatus";
        private const string ApplicationUserKey = "src.vs.platform.reactivestorage.browser.reactiveStorageServiceImpl.persistentStorage.applicationUser";

        public CursorDesktopAuth? TryLoad()
        {
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(applicationData))
            {
                return null;
            }

            var databasePath = Path.Combine(applicationData, "Cursor", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(databasePath))
            {
                return null;
            }

            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared
                };

                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();

                var accessToken = TryReadValue(connection, AccessTokenKey);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    return null;
                }

                return new CursorDesktopAuth(
                    accessToken.Trim(),
                    TryReadBackendUrl(connection) ?? "https://api2.cursor.sh",
                    TryReadValue(connection, MembershipTypeKey),
                    TryReadValue(connection, SubscriptionStatusKey));
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        private static string? TryReadBackendUrl(SqliteConnection connection)
        {
            var applicationUser = TryReadValue(connection, ApplicationUserKey);
            if (string.IsNullOrWhiteSpace(applicationUser))
            {
                return null;
            }

            using var document = JsonDocument.Parse(applicationUser);
            if (!document.RootElement.TryGetProperty("cursorCreds", out var cursorCreds) ||
                cursorCreds.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var backendUrl = TryGetString(cursorCreds, "backendUrl");
            return Uri.TryCreate(backendUrl, UriKind.Absolute, out var parsed) &&
                   parsed.Scheme is "https" or "http"
                ? parsed.ToString().TrimEnd('/')
                : null;
        }

        private static string? TryReadValue(SqliteConnection connection, string key)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }
}
