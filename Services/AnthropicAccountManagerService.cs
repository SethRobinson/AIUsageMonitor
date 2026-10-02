using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

// Creates and maintains managed Claude accounts: each one is a dedicated CLAUDE_CONFIG_DIR
// under %LOCALAPPDATA%\SethsAIUsageMonitor\accounts\anthropic that the official claude CLI
// logs into once; afterwards the app reads and refreshes that dir's .credentials.json itself.
public sealed class AnthropicAccountManagerService
{
    private static readonly TimeSpan LoginPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly AppSettingsService _settingsService;
    private readonly AppLogService _logService;
    private readonly HttpClient _httpClient;
    private readonly ClaudeBillingService _billingService;

    public AnthropicAccountManagerService(
        AppSettingsService settingsService,
        AppLogService logService,
        HttpClient? httpClient = null,
        string? accountsRootDirectory = null,
        ClaudeBillingService? billingService = null)
    {
        _settingsService = settingsService;
        _logService = logService;
        _httpClient = httpClient ?? SharedHttpClient;
        _billingService = billingService ?? new ClaudeBillingService(settingsService);
        AccountsRootDirectory = accountsRootDirectory ?? DefaultAccountsRootDirectory;
    }

    public static string DefaultAccountsRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SethsAIUsageMonitor",
        "accounts",
        "anthropic");

    public string AccountsRootDirectory { get; }

    public ProviderAccount CreateAccount(string label)
    {
        var slug = BuildSlug(label);
        var configDir = Path.Combine(AccountsRootDirectory, slug);
        Directory.CreateDirectory(configDir);

        var account = new ProviderAccount
        {
            Id = $"anthropic-{slug}",
            ProviderName = KnownProviders.Anthropic,
            Label = label.Trim(),
            Enabled = true,
            IsDefault = false,
            ConfigDir = configDir
        };

        _settingsService.Update(settings => settings.ProviderAccounts.Add(account));
        _logService.Info("Anthropic", $"Created managed Claude account '{account.Label}' at {configDir}.");
        return account;
    }

    public bool RemoveAccount(string accountId)
    {
        ProviderAccount? account = null;
        _settingsService.Update(settings =>
        {
            account = settings.ProviderAccounts.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, accountId, StringComparison.OrdinalIgnoreCase) && !candidate.IsDefault);
            if (account is not null) settings.ProviderAccounts.Remove(account);
        });
        if (account is null)
        {
            return false;
        }

        if (account.ClaudeBilling is { } connection)
        {
            try { _billingService.DeleteProfileAsync(connection.Id).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _logService.Warning("Anthropic", "Removed the billing connection, but its browser profile could not be deleted."); }
        }

        // Only ever delete dirs we created under our own accounts root; never ~/.claude.
        if (!string.IsNullOrWhiteSpace(account.ConfigDir) &&
            Path.GetFullPath(account.ConfigDir).StartsWith(Path.GetFullPath(AccountsRootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(account.ConfigDir))
        {
            try
            {
                Directory.Delete(account.ConfigDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logService.Error("Anthropic", $"Could not delete account directory {account.ConfigDir}: {ex.Message}");
            }
        }

        _logService.Info("Anthropic", $"Removed managed Claude account '{account.Label}'.");
        return true;
    }

    public bool RenameAccount(string accountId, string newLabel)
    {
        if (string.IsNullOrWhiteSpace(newLabel))
        {
            return false;
        }

        var found = false;
        _settingsService.Update(settings =>
        {
            var account = settings.ProviderAccounts.FirstOrDefault(candidate => candidate.Id == accountId);
            if (account is null) return;
            account.Label = newLabel.Trim();
            found = true;
        });
        return found;
    }

    public bool SetAccountEnabled(string accountId, bool enabled)
    {
        var found = false;
        _settingsService.Update(settings =>
        {
            var account = settings.ProviderAccounts.FirstOrDefault(candidate => candidate.Id == accountId);
            if (account is null) return;
            account.Enabled = enabled;
            found = true;
        });
        return found;
    }

    // Launches the official claude CLI in a visible terminal with CLAUDE_CONFIG_DIR pointed
    // at the account dir, then waits for a (new) .credentials.json to appear. Returns the
    // account with identity filled in on success, null on timeout/cancel/missing CLI.
    public async Task<LoginResult> LaunchLoginAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.ConfigDir))
        {
            return LoginResult.Failed("This account has no config directory.");
        }

        var claudePath = CommandPathResolver.FindCommandPath("claude");
        if (string.IsNullOrWhiteSpace(claudePath))
        {
            return LoginResult.Failed("The claude CLI was not found on PATH. Install Claude Code first.");
        }

        Directory.CreateDirectory(account.ConfigDir);
        var credentialsPath = Path.Combine(account.ConfigDir, ".credentials.json");
        var baselineWriteTime = File.Exists(credentialsPath)
            ? File.GetLastWriteTimeUtc(credentialsPath)
            : (DateTime?)null;

        try
        {
            // /k keeps the window open so the user can see errors; the env var has to be set
            // inline because UseShellExecute ignores ProcessStartInfo.Environment. If this CLI
            // version doesn't auto-run /login as a prompt argument the window stays open and
            // the user can type /login themselves.
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"set CLAUDE_CONFIG_DIR={account.ConfigDir}&& \"{claudePath}\" /login\"",
                UseShellExecute = true
            };
            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return LoginResult.Failed($"Could not launch the claude CLI: {ex.Message}");
        }

        var deadline = DateTimeOffset.UtcNow + LoginTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(LoginPollInterval, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(credentialsPath))
            {
                continue;
            }

            var writeTime = File.GetLastWriteTimeUtc(credentialsPath);
            if (baselineWriteTime is null || writeTime > baselineWriteTime)
            {
                var identity = await TryFetchIdentityAsync(credentialsPath, cancellationToken).ConfigureAwait(false);
                PersistLoginIdentity(account, identity);
                return LoginResult.Successful(identity?.Email ?? string.Empty);
            }
        }

        return LoginResult.Failed(
            "Timed out waiting for the login to complete. Finish /login in the terminal window and use 'Log in again' to retry detection.");
    }

    internal static string? TryReadAccessToken(string credentialsPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(credentialsPath));
            if (document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.ValueKind == JsonValueKind.Object &&
                oauth.TryGetProperty("accessToken", out var tokenElement))
            {
                return tokenElement.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public async Task<AccountIdentity?> TryFetchIdentityAsync(string credentialsPath, CancellationToken cancellationToken)
    {
        var accessToken = TryReadAccessToken(credentialsPath);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseIdentity(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // Rebuilds the `oauthAccount` block Claude Code caches in .claude.json (the block
    // /status displays) from the live profile endpoint, keyed by whatever token the given
    // credentials file holds. Used to heal a stale or missing block during account switches.
    public async Task<JsonObject?> TryFetchOAuthAccountBlockAsync(string credentialsPath, CancellationToken cancellationToken)
    {
        var accessToken = TryReadAccessToken(credentialsPath);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var block = new JsonObject();

            if (root.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object)
            {
                AddStringProperty(block, "accountUuid", account, "uuid");
                AddStringProperty(block, "emailAddress", account, "email");
                AddStringProperty(block, "displayName", account, "display_name");
            }

            if (root.TryGetProperty("organization", out var organization) && organization.ValueKind == JsonValueKind.Object)
            {
                AddStringProperty(block, "organizationUuid", organization, "uuid");
                AddStringProperty(block, "organizationName", organization, "name");
                AddStringProperty(block, "organizationType", organization, "organization_type");
                AddStringProperty(block, "organizationRateLimitTier", organization, "rate_limit_tier");
                AddStringProperty(block, "billingType", organization, "billing_type");
                if (organization.TryGetProperty("has_extra_usage_enabled", out var extraUsage) &&
                    extraUsage.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    block["hasExtraUsageEnabled"] = extraUsage.GetBoolean();
                }
            }

            return block["accountUuid"] is null ? null : block;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static void AddStringProperty(JsonObject block, string blockKey, JsonElement source, string sourceKey)
    {
        if (source.TryGetProperty(sourceKey, out var element) && element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                block[blockKey] = value;
            }
        }
    }

    internal static AccountIdentity? ParseIdentity(JsonElement profileRoot)
    {
        if (profileRoot.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var email = string.Empty;
        var uuid = string.Empty;

        if (profileRoot.TryGetProperty("account", out var accountElement) &&
            accountElement.ValueKind == JsonValueKind.Object)
        {
            if (accountElement.TryGetProperty("email", out var emailElement))
            {
                email = emailElement.GetString() ?? string.Empty;
            }

            if (accountElement.TryGetProperty("uuid", out var uuidElement))
            {
                uuid = uuidElement.GetString() ?? string.Empty;
            }
            else if (accountElement.TryGetProperty("id", out var idElement))
            {
                uuid = idElement.GetString() ?? string.Empty;
            }
        }

        return string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(uuid)
            ? null
            : new AccountIdentity(email, uuid);
    }

    private void PersistLoginIdentity(ProviderAccount account, AccountIdentity? identity)
    {
        ProviderAccount? saved = null;
        _settingsService.Update(settings =>
        {
            saved = settings.ProviderAccounts.FirstOrDefault(candidate => candidate.Id == account.Id);
            if (saved is null || identity is null) return;
            saved.Email = identity.Email;
            saved.AccountUuid = identity.Uuid;
            if (saved.ClaudeBilling is { } billing && billing.AccountUuid != identity.Uuid)
            {
                billing.State = ClaudeBillingConnectionState.ReconnectRequired;
                billing.Message = "Claude account changed. Reconnect usage credits.";
            }
            if (string.IsNullOrWhiteSpace(saved.Label) && !string.IsNullOrWhiteSpace(identity.Email)) saved.Label = identity.Email;
        });
        if (saved is null) return;
        account.Email = saved.Email;
        account.AccountUuid = saved.AccountUuid;
        account.Label = saved.Label;
        _logService.Info(
            "Anthropic",
            $"Managed Claude account '{saved.Label}' logged in{(string.IsNullOrWhiteSpace(saved.Email) ? "" : $" as {saved.Email}")}.");
    }

    private static string BuildSlug(string label)
    {
        var cleaned = new string(label
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
        if (cleaned.Length > 24)
        {
            cleaned = cleaned[..24].Trim('-');
        }

        if (cleaned.Length == 0)
        {
            cleaned = "account";
        }

        return $"{cleaned}-{Guid.NewGuid().ToString("N")[..4]}";
    }

    public sealed record AccountIdentity(string Email, string Uuid);

    public sealed record LoginResult(bool Succeeded, string Message)
    {
        public static LoginResult Successful(string email) => new(true, email);

        public static LoginResult Failed(string message) => new(false, message);
    }
}
