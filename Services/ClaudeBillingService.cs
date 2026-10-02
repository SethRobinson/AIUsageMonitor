using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed record ClaudeBillingResult(UsageWindow? Balance, ClaudeBillingConnectionState State, string Message);

// Separate from quota collection so a future OAuth balance source can replace web authentication.
public interface IClaudeBillingReader
{
    Task<ClaudeBillingResult> ReadAsync(ProviderAccount account, string credentialsPath, CancellationToken cancellationToken);
}

public sealed class ClaudeBillingService : IClaudeBillingReader
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AppSettingsService _settings;
    private readonly HttpClient _client;
    private readonly string _profileRoot;
    private readonly IClaudeBillingBrowser _browser;

    public ClaudeBillingService(AppSettingsService settings, HttpClient? client = null, string? profileRoot = null,
        IClaudeBillingBrowser? browser = null)
    {
        _settings = settings;
        _client = client ?? SharedClient;
        _browser = browser ?? new ClaudeBillingBrowser();
        _profileRoot = Path.GetFullPath(profileRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SethsAIUsageMonitor", "ClaudeBillingWebView2"));
    }

    public string GetProfileDirectory(string connectionId)
    {
        if (!Guid.TryParseExact(connectionId, "N", out var id)) throw new InvalidDataException("Invalid billing profile ID.");
        return Path.Combine(_profileRoot, id.ToString("N"));
    }

    public async Task DeleteProfileAsync(string connectionId)
    {
        var directory = Path.GetFullPath(GetProfileDirectory(connectionId));
        if (!directory.StartsWith(_profileRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Billing profile must remain inside its root.");
        await ClaudeBillingBrowser.StopProfileAsync(directory).ConfigureAwait(false);
        // WebView2 can take a moment to release files after its window closes.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    public async Task DisconnectAsync(string accountId)
    {
        string? profileId = null;
        _settings.Update(settings =>
        {
            var account = settings.ProviderAccounts.FirstOrDefault(a => a.Id == accountId && a.ProviderName == KnownProviders.Anthropic);
            profileId = account?.ClaudeBilling?.Id;
            if (account is not null) account.ClaudeBilling = null;
        });
        if (!string.IsNullOrEmpty(profileId)) await DeleteProfileAsync(profileId).ConfigureAwait(false);
    }

    public static string CredentialsPath(ProviderAccount account, string? homeDirectory = null) => Path.Combine(
        account.IsDefault ? Path.Combine(homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : account.ConfigDir,
        ".credentials.json");

    public async Task<ClaudeBillingConnection> ConnectAsync(ProviderAccount account, string cookieHeader,
        string connectionId, CancellationToken cancellationToken, string userAgent = "", IClaudeBillingWebSession? browserSession = null)
    {
        _ = GetProfileDirectory(connectionId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        var path = CredentialsPath(account);
        if (!account.IsDefault)
        {
            await new AnthropicOAuthTokenRefresher(httpClient: _client)
                .GetFreshAccessTokenAsync(path, false, cancellationToken).ConfigureAwait(false);
        }
        var token = ReadToken(path);
        var identity = await ReadOAuthIdentityAsync(token, cancellationToken).ConfigureAwait(false);
        EnsureExpectedIdentity(account, identity);
        // Setup borrows the visible browser; tests and background reads inject/open the same transport.
        await using var ownedSession = browserSession is null
            ? await _browser.OpenAsync(GetProfileDirectory(connectionId), cancellationToken).ConfigureAwait(false) : null;
        var session = browserSession ?? ownedSession!;
        await VerifyWebIdentityAsync(session, identity, cancellationToken).ConfigureAwait(false);
        var balance = await ReadBalanceAsync(session, identity.OrganizationUuid, cancellationToken).ConfigureAwait(false);
        EnsureTokenUnchanged(path, token);

        var connection = new ClaudeBillingConnection
        {
            Id = connectionId, AccountUuid = identity.AccountUuid, OrganizationUuid = identity.OrganizationUuid,
            CookieHeaderProtected = ProtectedStringService.Protect(cookieHeader), UserAgent = userAgent,
            State = ClaudeBillingConnectionState.Connected, Message = $"Connected: {balance.RemainingText}", LastCheckedAt = DateTimeOffset.Now
        };
        _settings.Update(settings =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = settings.ProviderAccounts.FirstOrDefault(a => a.Id == account.Id && a.ProviderName == KnownProviders.Anthropic);
            if (current is null || current.AccountUuid != account.AccountUuid || current.ConfigDir != account.ConfigDir ||
                current.ClaudeBilling?.Id != account.ClaudeBilling?.Id)
                throw new InvalidOperationException("This account changed while connecting. Close this window and try again.");
            current.ClaudeBilling = connection;
        });
        return connection;
    }

    public async Task<ClaudeBillingResult> ReadAsync(ProviderAccount account, string credentialsPath, CancellationToken cancellationToken)
    {
        var connection = account.ClaudeBilling ?? throw new InvalidOperationException("Billing is not connected.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var requestToken = deadline.Token;
        try
        {
            var token = ReadToken(credentialsPath);
            var identity = await ReadOAuthIdentityAsync(token, requestToken).ConfigureAwait(false);
            EnsureExpectedIdentity(account, identity);
            if (!SameId(identity.AccountUuid, connection.AccountUuid) || !SameId(identity.OrganizationUuid, connection.OrganizationUuid))
                throw Reconnect("Claude account or organization changed. Reconnect usage credits.");
            await using var session = await _browser.OpenAsync(GetProfileDirectory(connection.Id), requestToken).ConfigureAwait(false);
            await VerifyWebIdentityAsync(session, identity, requestToken).ConfigureAwait(false);
            var balance = await ReadBalanceAsync(session, identity.OrganizationUuid, requestToken).ConfigureAwait(false);
            EnsureTokenUnchanged(credentialsPath, token);
            return new(balance, ClaudeBillingConnectionState.Connected, $"Connected: {balance.RemainingText}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ClaudeBillingException ex) { return new(null, ex.State, ex.Message); }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return new(null, ClaudeBillingConnectionState.ReconnectRequired, "Saved billing login could not be read. Reconnect usage credits.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            return new(null, ClaudeBillingConnectionState.TemporarilyUnavailable, "Usage credits temporarily unavailable; quota monitoring continues.");
        }
    }

    private async Task<BillingIdentity> ReadOAuthIdentityAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var json = await ReadJsonAsync(request, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var account = root.GetProperty("account").GetProperty("uuid").GetString();
        var organization = root.GetProperty("organization").GetProperty("uuid").GetString();
        if (string.IsNullOrWhiteSpace(account) || !Guid.TryParse(organization, out _))
            throw Reconnect("Could not verify this Claude account and organization.");
        return new(account, organization!);
    }

    private static void EnsureExpectedIdentity(ProviderAccount account, BillingIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(account.AccountUuid) || !SameId(account.AccountUuid, identity.AccountUuid))
            throw Reconnect("The Claude login no longer matches this saved account. Log the account in again first.");
    }

    private static async Task VerifyWebIdentityAsync(IClaudeBillingWebSession session, BillingIdentity expected, CancellationToken cancellationToken)
    {
        using var account = await ReadWebJsonAsync("/api/account", session, cancellationToken).ConfigureAwait(false);
        var root = account.RootElement;
        if (!root.TryGetProperty("uuid", out var uuid) || uuid.ValueKind != JsonValueKind.String || !SameId(uuid.GetString(), expected.AccountUuid))
            throw Reconnect("This website login belongs to a different Claude account. Sign in to the account shown above.");
        using var organizations = await ReadWebJsonAsync("/api/organizations", session, cancellationToken).ConfigureAwait(false);
        if (organizations.RootElement.ValueKind != JsonValueKind.Array || !organizations.RootElement.EnumerateArray().Any(org =>
                org.ValueKind == JsonValueKind.Object && org.TryGetProperty("uuid", out var id) &&
                id.ValueKind == JsonValueKind.String && SameId(id.GetString(), expected.OrganizationUuid)))
            throw Reconnect("The website login does not have the Claude organization used by this account.");
    }

    private static async Task<UsageWindow> ReadBalanceAsync(IClaudeBillingWebSession session, string organization, CancellationToken cancellationToken)
    {
        using var json = await ReadWebJsonAsync($"/api/organizations/{Uri.EscapeDataString(organization)}/prepaid/credits", session, cancellationToken).ConfigureAwait(false);
        return ParseBalance(json.RootElement);
    }

    internal static UsageWindow ParseBalance(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("amount", out var amount) ||
            amount.ValueKind != JsonValueKind.Number || !amount.TryGetDecimal(out var minor) ||
            !root.TryGetProperty("currency", out var currencyElement) || currencyElement.ValueKind != JsonValueKind.String)
            throw Temporary("Claude did not provide a usage-credit balance.");
        var currency = currencyElement.GetString()!.ToUpperInvariant();
        var exponent = currency switch
        {
            "JPY" or "KRW" => 0,
            "BHD" or "KWD" or "OMR" or "JOD" or "TND" => 3,
            "USD" or "EUR" or "GBP" or "CAD" or "AUD" or "NZD" or "CHF" or "HKD" or "SGD" => 2,
            _ => -1
        };
        if (root.TryGetProperty("decimal_places", out var places))
        {
            if (places.ValueKind != JsonValueKind.Number || !places.TryGetInt32(out exponent)) exponent = -1;
        }
        if (exponent is < 0 or > 6 || currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))
            throw Temporary("Claude returned an unsupported balance currency.");
        for (var i = 0; i < exponent; i++) minor /= 10m;
        return ClaudeUsageCredits.CreateBalance(minor, currency, exponent, "Prepaid Claude usage balance, used after plan limits");
    }

    private static async Task<JsonDocument> ReadWebJsonAsync(string path, IClaudeBillingWebSession session, CancellationToken cancellationToken)
    {
        var response = await session.GetAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response.Status, response.Content);
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseResponse((int)response.StatusCode, content);
    }

    private static JsonDocument ParseResponse(int status, string content)
    {
        // Manual fetch redirects have status 0 (opaque redirect) rather than exposing login HTML.
        if (status is 0 or 401 or (>= 300 and < 400))
            throw Reconnect("Claude billing login expired. Reconnect usage credits.");
        if (status == 403)
            throw content.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) || content.Contains("challenge", StringComparison.OrdinalIgnoreCase)
                ? Temporary("Claude needs a browser check. Finish the check in the credits connection window, then click Connect.")
                : Reconnect("Claude billing access was rejected. Reconnect usage credits.");
        if (status is < 200 or >= 300) throw Temporary($"Usage credits temporarily unavailable (HTTP {status}).");
        if (content.TrimStart().StartsWith('<')) throw Temporary("Claude billing returned a browser page. Open Reconnect credits to retry.");
        return JsonDocument.Parse(content);
    }

    private static string ReadToken(string path) => AnthropicAccountManagerService.TryReadAccessToken(path) is { Length: > 0 } token
        ? token : throw Reconnect("Claude login is missing. Log this account in again first.");
    internal static string CredentialFingerprint(string path)
    {
        var token = AnthropicAccountManagerService.TryReadAccessToken(path);
        return string.IsNullOrWhiteSpace(token) ? string.Empty : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }
    private static void EnsureTokenUnchanged(string path, string token)
    {
        if (ReadToken(path) != token) throw Temporary("Claude login changed during billing refresh. Retrying on the next refresh.");
    }
    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static ClaudeBillingException Reconnect(string message) => new(ClaudeBillingConnectionState.ReconnectRequired, message);
    private static ClaudeBillingException Temporary(string message) => new(ClaudeBillingConnectionState.TemporarilyUnavailable, message);
    private sealed record BillingIdentity(string AccountUuid, string OrganizationUuid);
}

public sealed class ClaudeBillingException(ClaudeBillingConnectionState state, string message) : Exception(message)
{
    public ClaudeBillingConnectionState State { get; } = state;
}
