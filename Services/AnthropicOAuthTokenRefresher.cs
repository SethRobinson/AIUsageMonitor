using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIUsageMonitor.Services;

// Refreshes a Claude Code OAuth access token stored in a .credentials.json file and writes
// the rotated tokens back, mirroring how GeminiUsageCollector refreshes the Gemini CLI's
// oauth_creds.json. Needed for managed accounts: no CLI runs against those config dirs
// day-to-day, so nobody else keeps their tokens alive.
public sealed class AnthropicOAuthTokenRefresher
{
    public const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";
    // Claude Code's public OAuth client id. Like the Gemini CLI's client id this ships in
    // the public CLI and is not a secret; possession of it grants nothing without the
    // user's own refresh token.
    public const string ClaudeCodeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);
    private const int WriteRetryCount = 3;
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    // Per-credentials-path locks so concurrent collectors never interleave a
    // read-refresh-write cycle on the same file.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly AppLogService? _logService;

    public AnthropicOAuthTokenRefresher(HttpClient? httpClient = null, AppLogService? logService = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _logService = logService;
    }

    // Returns a usable access token, refreshing and persisting it first when it is expired
    // (or forceRefresh is set). Returns the stale token when the refresh fails transiently
    // (the caller's normal 401 handling applies) and null when the refresh token itself was
    // rejected, which means the account needs a fresh login.
    public async Task<string?> GetFreshAccessTokenAsync(
        string credentialsPath,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var fileLock = FileLocks.GetOrAdd(credentialsPath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var credentials = ReadCredentials(credentialsPath);
            if (credentials?["claudeAiOauth"] is not JsonObject oauth)
            {
                return null;
            }

            var accessToken = oauth["accessToken"]?.GetValue<string>();
            var refreshToken = oauth["refreshToken"]?.GetValue<string>();
            var expiresAtMs = TryGetInt64(oauth["expiresAt"]);

            if (!forceRefresh && !IsExpired(expiresAtMs) && !string.IsNullOrWhiteSpace(accessToken))
            {
                return accessToken;
            }

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return accessToken;
            }

            var refreshed = await RequestRefreshedTokensAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            if (refreshed.IsRejected)
            {
                _logService?.Error(
                    "Anthropic",
                    $"OAuth refresh token was rejected for {credentialsPath}. The account needs a new login.");
                return null;
            }

            if (refreshed.Tokens is null)
            {
                // Transient failure (network, 5xx): fall back to whatever we have.
                return accessToken;
            }

            oauth["accessToken"] = refreshed.Tokens.AccessToken;
            if (!string.IsNullOrWhiteSpace(refreshed.Tokens.RefreshToken))
            {
                oauth["refreshToken"] = refreshed.Tokens.RefreshToken;
            }

            oauth["expiresAt"] = DateTimeOffset.UtcNow
                .AddSeconds(refreshed.Tokens.ExpiresInSeconds)
                .ToUnixTimeMilliseconds();

            if (!TryWriteCredentials(credentialsPath, credentials))
            {
                _logService?.Error(
                    "Anthropic",
                    $"Failed to persist rotated OAuth tokens to {credentialsPath}. " +
                    "If the token stops working, log the account in again from Settings.");
            }

            return refreshed.Tokens.AccessToken;
        }
        finally
        {
            fileLock.Release();
        }
    }

    private static bool IsExpired(long? expiresAtUnixMs)
    {
        if (expiresAtUnixMs is null)
        {
            return true;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs.Value);
        return expiresAt <= DateTimeOffset.UtcNow.Add(ExpiryMargin);
    }

    private async Task<RefreshOutcome> RequestRefreshedTokensAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClaudeCodeClientId
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return RefreshOutcome.Transient;
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return RefreshOutcome.Rejected;
            }

            if (!response.IsSuccessStatusCode)
            {
                return RefreshOutcome.Transient;
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;

                var accessToken = root.TryGetProperty("access_token", out var accessElement)
                    ? accessElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    return RefreshOutcome.Transient;
                }

                var newRefreshToken = root.TryGetProperty("refresh_token", out var refreshElement)
                    ? refreshElement.GetString() ?? string.Empty
                    : string.Empty;
                var expiresInSeconds = root.TryGetProperty("expires_in", out var expiresElement) &&
                    expiresElement.TryGetInt64(out var expiresIn)
                    ? expiresIn
                    : 3600;

                return RefreshOutcome.Success(new RefreshedTokens(accessToken, newRefreshToken, expiresInSeconds));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return RefreshOutcome.Transient;
            }
        }
    }

    private static JsonObject? ReadCredentials(string credentialsPath)
    {
        try
        {
            if (!File.Exists(credentialsPath))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(credentialsPath)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryWriteCredentials(string credentialsPath, JsonObject credentials)
    {
        var json = credentials.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var tempPath = credentialsPath + ".tmp";

        for (var attempt = 1; attempt <= WriteRetryCount; attempt++)
        {
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, credentialsPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == WriteRetryCount)
                {
                    return false;
                }

                Thread.Sleep(WriteRetryDelay);
            }
        }

        return false;
    }

    private static long? TryGetInt64(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<long>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private sealed record RefreshedTokens(string AccessToken, string RefreshToken, long ExpiresInSeconds);

    private sealed record RefreshOutcome(bool IsRejected, RefreshedTokens? Tokens)
    {
        public static readonly RefreshOutcome Rejected = new(true, null);
        public static readonly RefreshOutcome Transient = new(false, null);

        public static RefreshOutcome Success(RefreshedTokens tokens) => new(false, tokens);
    }
}
