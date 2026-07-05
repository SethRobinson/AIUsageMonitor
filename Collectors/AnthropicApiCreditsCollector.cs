using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Collectors;

public sealed class AnthropicApiCreditsCollector : IUsageCollector
{
    internal const string ConsoleBaseUrl = "https://platform.claude.com";
    internal const string LegacyConsoleBaseUrl = "https://console.anthropic.com";
    internal const decimal MaxSupportedCents = 100_000_000m;
    private static readonly TimeSpan CacheFallbackMaxAge = TimeSpan.FromDays(7);

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly AppSettingsService _settingsService;
    private readonly HttpClient _httpClient;

    public AnthropicApiCreditsCollector(AppSettingsService settingsService, HttpClient? httpClient = null)
    {
        _settingsService = settingsService;
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public string ProviderName => KnownProviders.AnthropicApiCredits;

    public async Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.Load();

        string cookieHeader;
        try
        {
            cookieHeader = ProtectedStringService.Unprotect(settings.AnthropicApiCreditsCookieHeaderProtected);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return ProviderUsageFactory.Unavailable(
                ProviderName,
                $"Saved Anthropic Console login could not be decrypted: {ex.Message}. Re-save it from Anthropic API Credits Setup.",
                "Anthropic Console billing");
        }

        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return ProviderUsageFactory.Unavailable(
                ProviderName,
                "Anthropic Console billing login is not saved. Open Settings, enable Anthropic API Credits, then use Setup.",
                "Anthropic Console billing");
        }

        if (string.IsNullOrWhiteSpace(settings.AnthropicApiCreditsOrganizationUuid))
        {
            return ProviderUsageFactory.Unavailable(
                ProviderName,
                "Anthropic Console organization is not saved. Re-save Anthropic API Credits setup.",
                "Anthropic Console billing");
        }

        try
        {
            var snapshot = await FetchSnapshotAsync(
                _httpClient,
                cookieHeader,
                settings.AnthropicApiCreditsOrganizationUuid,
                settings.AnthropicApiCreditsOrganizationName,
                cancellationToken).ConfigureAwait(false);
            SaveBalanceCache(snapshot);
            return CreateProviderUsage(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AnthropicApiCreditsException ex) when (ex.Kind is AnthropicApiCreditsFailureKind.AuthExpired)
        {
            return ProviderUsageFactory.Unavailable(
                ProviderName,
                "Anthropic Console login expired. Re-save Anthropic API Credits setup.",
                "Anthropic Console billing");
        }
        catch (AnthropicApiCreditsException ex) when (ex.Kind is AnthropicApiCreditsFailureKind.NotFound)
        {
            return ProviderUsageFactory.Unavailable(
                ProviderName,
                "Anthropic billing organization was not found or the Console billing endpoint changed. Re-save Anthropic API Credits setup.",
                "Anthropic Console billing");
        }
        catch (Exception ex) when (ex is AnthropicApiCreditsException or HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return TryCreateProviderFromCache(settings, $"refresh failed: {ex.Message}") ??
                   ProviderUsageFactory.Unavailable(
                       ProviderName,
                       $"Anthropic API credits refresh failed and no recent cached balance is available: {ex.Message}",
                       "Anthropic Console billing");
        }
    }

    internal static async Task<AnthropicApiCreditsSnapshot> FetchSnapshotAsync(
        HttpClient httpClient,
        string cookieHeader,
        string organizationUuid,
        string organizationName,
        CancellationToken cancellationToken)
    {
        using var creditsRequest = BuildConsoleRequest(
            HttpMethod.Get,
            $"{ConsoleBaseUrl}/api/organizations/{organizationUuid}/prepaid/credits",
            cookieHeader);
        using var creditsResponse = await httpClient.SendAsync(creditsRequest, cancellationToken).ConfigureAwait(false);
        var creditsJson = await ReadSuccessfulJsonAsync(creditsResponse, cancellationToken).ConfigureAwait(false);

        string? expiryJson = null;
        try
        {
            using var expiryRequest = BuildConsoleRequest(
                HttpMethod.Get,
                $"{ConsoleBaseUrl}/api/organizations/{organizationUuid}/prepaid/credit_expiry",
                cookieHeader);
            using var expiryResponse = await httpClient.SendAsync(expiryRequest, cancellationToken).ConfigureAwait(false);
            expiryJson = await ReadSuccessfulJsonAsync(expiryResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AnthropicApiCreditsException or HttpRequestException or TaskCanceledException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expiryJson = null;
        }

        return ParseSnapshot(creditsJson, expiryJson, organizationUuid, organizationName, DateTimeOffset.Now);
    }

    internal static HttpRequestMessage BuildConsoleRequest(HttpMethod method, string url, string cookieHeader)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.Referrer = new Uri($"{ConsoleBaseUrl}/settings/billing");
        return request;
    }

    internal static AnthropicApiCreditsSnapshot ParseSnapshot(
        string creditsJson,
        string? expiryJson,
        string organizationUuid,
        string organizationName,
        DateTimeOffset verifiedAt)
    {
        if (LooksLikeHtml(creditsJson))
        {
            throw new AnthropicApiCreditsException(
                AnthropicApiCreditsFailureKind.AuthExpired,
                "Anthropic Console returned an HTML login page.");
        }

        try
        {
            using var creditsDocument = JsonDocument.Parse(creditsJson);
            var root = creditsDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Anthropic Console billing response was {root.ValueKind}, not an object.");
            }

            var amountCents = ReadRequiredCents(root, "amount");
            var pendingInvoiceAmountCents = ReadOptionalCents(root, "pending_invoice_amount_cents");

            decimal? expiringAmountCents = null;
            DateTimeOffset? expiresAt = null;
            if (!string.IsNullOrWhiteSpace(expiryJson) && !LooksLikeHtml(expiryJson))
            {
                try
                {
                    using var expiryDocument = JsonDocument.Parse(expiryJson);
                    var expiryRoot = expiryDocument.RootElement;
                    if (expiryRoot.ValueKind == JsonValueKind.Object)
                    {
                        expiringAmountCents = ReadOptionalCents(expiryRoot, "remaining_amount_cents");
                        expiresAt = ReadOptionalDateTimeOffset(expiryRoot, "expires_at");
                    }
                }
                catch (JsonException)
                {
                    expiringAmountCents = null;
                    expiresAt = null;
                }
            }

            return new AnthropicApiCreditsSnapshot(
                amountCents,
                pendingInvoiceAmountCents,
                expiringAmountCents,
                expiresAt,
                verifiedAt,
                organizationUuid,
                organizationName);
        }
        catch (JsonException ex)
        {
            throw new AnthropicApiCreditsException(
                AnthropicApiCreditsFailureKind.Schema,
                $"Anthropic Console billing JSON was invalid: {ex.Message}",
                ex);
        }
        catch (InvalidDataException ex)
        {
            throw new AnthropicApiCreditsException(
                AnthropicApiCreditsFailureKind.Schema,
                ex.Message,
                ex);
        }
    }

    internal static ProviderUsage CreateProviderUsage(AnthropicApiCreditsSnapshot snapshot, string staleFailure = "")
    {
        var isUnpaid = snapshot.AmountCents < 0;
        var balanceText = isUnpaid
            ? $"Unpaid balance {FormatUsdFromCents(Math.Abs(snapshot.AmountCents))}"
            : $"{FormatUsdFromCents(snapshot.AmountCents)} left";
        var detailParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(snapshot.OrganizationName))
        {
            detailParts.Add(snapshot.OrganizationName);
        }

        if (snapshot.PendingInvoiceAmountCents is > 0)
        {
            detailParts.Add($"{FormatUsdFromCents(snapshot.PendingInvoiceAmountCents.Value)} pending this period");
        }

        if (snapshot.ExpiringAmountCents is > 0 && snapshot.ExpiresAt is { } expiresAt)
        {
            detailParts.Add($"{FormatUsdFromCents(snapshot.ExpiringAmountCents.Value)} expires {expiresAt.ToLocalTime():MMM d}");
        }

        detailParts.Add($"Verified {FormatRelativeAge(snapshot.VerifiedAt)}");

        if (!string.IsNullOrWhiteSpace(staleFailure))
        {
            detailParts.Add(staleFailure);
        }

        return new ProviderUsage
        {
            Name = KnownProviders.AnthropicApiCredits,
            PlanName = string.Empty,
            Source = "Anthropic Console billing",
            StatusMessage = string.IsNullOrWhiteSpace(staleFailure)
                ? isUnpaid
                    ? "Anthropic Console reports an unpaid API billing balance."
                    : "Anthropic Console prepaid API credit balance."
                : $"Showing last verified Anthropic API credit balance; {staleFailure}",
            Windows =
            [
                new UsageWindow
                {
                    Title = isUnpaid ? "Unpaid balance" : "Prepaid credits",
                    Limit = 100,
                    Used = isUnpaid ? 100 : 0,
                    Remaining = isUnpaid ? 0 : 100,
                    RemainingText = balanceText,
                    Detail = string.Join("; ", detailParts),
                    HideReset = true,
                    IsBalance = true
                }
            ]
        };
    }

    private static async Task<string> ReadSuccessfulJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AnthropicApiCreditsException(
                AnthropicApiCreditsFailureKind.AuthExpired,
                $"Anthropic Console rejected the saved login with HTTP {(int)response.StatusCode}.");
        }

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new AnthropicApiCreditsException(
                AnthropicApiCreditsFailureKind.NotFound,
                "Anthropic Console billing endpoint returned HTTP 404.");
        }

        if (response.StatusCode is HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500)
        {
            throw new AnthropicApiCreditsException(
                AnthropicApiCreditsFailureKind.Transient,
                $"Anthropic Console billing endpoint returned HTTP {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AnthropicApiCreditsException(
                AnthropicApiCreditsFailureKind.Transient,
                $"Anthropic Console billing endpoint returned HTTP {(int)response.StatusCode}.");
        }

        if (LooksLikeHtml(content))
        {
            throw new AnthropicApiCreditsException(
                AnthropicApiCreditsFailureKind.AuthExpired,
                "Anthropic Console returned an HTML login page.");
        }

        return content;
    }

    private static decimal ReadRequiredCents(JsonElement element, string propertyName)
    {
        if (!TryReadCents(element, propertyName, out var value))
        {
            throw new InvalidDataException($"Anthropic Console billing response did not include a valid {propertyName} field.");
        }

        return value;
    }

    private static decimal? ReadOptionalCents(JsonElement element, string propertyName)
    {
        return TryReadCents(element, propertyName, out var value) ? value : null;
    }

    private static bool TryReadCents(JsonElement element, string propertyName, out decimal value)
    {
        value = 0;

        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        var parsed = property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDecimal(out var number) ? number : (decimal?)null,
            JsonValueKind.String => decimal.TryParse(
                property.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var stringValue)
                ? stringValue
                : null,
            _ => null
        };

        if (!parsed.HasValue ||
            parsed.Value != decimal.Truncate(parsed.Value) ||
            Math.Abs(parsed.Value) > MaxSupportedCents)
        {
            return false;
        }

        value = parsed.Value;
        return true;
    }

    private static DateTimeOffset? ReadOptionalDateTimeOffset(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string FormatUsdFromCents(decimal cents)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${cents / 100m:0.00}");
    }

    private static string FormatRelativeAge(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.Now - timestamp.ToLocalTime();

        if (elapsed.TotalSeconds < 45)
        {
            return "now";
        }

        if (elapsed.TotalMinutes < 90)
        {
            var minutes = Math.Max(1, (int)Math.Round(elapsed.TotalMinutes));
            return minutes == 1 ? "1m ago" : $"{minutes}m ago";
        }

        if (elapsed.TotalHours < 36)
        {
            var hours = Math.Max(1, (int)Math.Round(elapsed.TotalHours));
            return hours == 1 ? "1h ago" : $"{hours}h ago";
        }

        var days = Math.Max(1, (int)Math.Round(elapsed.TotalDays));
        return days == 1 ? "1d ago" : $"{days}d ago";
    }

    private static bool LooksLikeHtml(string content)
    {
        var trimmed = content.TrimStart();
        return trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private void SaveBalanceCache(AnthropicApiCreditsSnapshot snapshot)
    {
        var settings = _settingsService.Load();
        settings.AnthropicApiCreditsLastBalance = snapshot.ToCache();
        _settingsService.Save(settings);
    }

    private static ProviderUsage? TryCreateProviderFromCache(AppSettings settings, string failure)
    {
        var cache = settings.AnthropicApiCreditsLastBalance;
        if (cache is null)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        if (cache.VerifiedAt == default ||
            now - cache.VerifiedAt > CacheFallbackMaxAge ||
            (!string.IsNullOrWhiteSpace(settings.AnthropicApiCreditsOrganizationUuid) &&
             !string.Equals(cache.OrganizationUuid, settings.AnthropicApiCreditsOrganizationUuid, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            var snapshot = AnthropicApiCreditsSnapshot.FromCache(cache);
            return CreateProviderUsage(snapshot, $"last verified {FormatRelativeAge(snapshot.VerifiedAt)}; {failure}");
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

internal sealed record AnthropicApiCreditsSnapshot(
    decimal AmountCents,
    decimal? PendingInvoiceAmountCents,
    decimal? ExpiringAmountCents,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset VerifiedAt,
    string OrganizationUuid,
    string OrganizationName)
{
    public AnthropicApiCreditsBalanceCache ToCache()
    {
        return new AnthropicApiCreditsBalanceCache
        {
            AmountCents = AmountCents,
            PendingInvoiceAmountCents = PendingInvoiceAmountCents,
            ExpiringAmountCents = ExpiringAmountCents,
            ExpiresAt = ExpiresAt,
            VerifiedAt = VerifiedAt,
            OrganizationUuid = OrganizationUuid,
            OrganizationName = OrganizationName
        };
    }

    public static AnthropicApiCreditsSnapshot FromCache(AnthropicApiCreditsBalanceCache cache)
    {
        if (Math.Abs(cache.AmountCents) > AnthropicApiCreditsCollector.MaxSupportedCents ||
            cache.AmountCents != decimal.Truncate(cache.AmountCents))
        {
            throw new InvalidDataException("Cached Anthropic API credits balance is outside the supported range.");
        }

        return new AnthropicApiCreditsSnapshot(
            cache.AmountCents,
            cache.PendingInvoiceAmountCents,
            cache.ExpiringAmountCents,
            cache.ExpiresAt,
            cache.VerifiedAt,
            cache.OrganizationUuid,
            cache.OrganizationName);
    }
}

internal enum AnthropicApiCreditsFailureKind
{
    AuthExpired,
    NotFound,
    Transient,
    Schema
}

internal sealed class AnthropicApiCreditsException : Exception
{
    public AnthropicApiCreditsException(
        AnthropicApiCreditsFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public AnthropicApiCreditsFailureKind Kind { get; }
}
