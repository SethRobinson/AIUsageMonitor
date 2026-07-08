using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

// Makes a managed account the active Claude CLI account by swapping its credentials into
// ~/.claude/.credentials.json (the approach community switcher tools use). The previous
// credentials are synced back to their managed account dir when we can identify them, and a
// timestamped backup is always written first. This switches the CLI only; the Claude Desktop
// app keeps its own session. Already-running claude sessions keep their old token until
// restarted.
//
// Claude Code stores identity in TWO files that must be swapped together: the OAuth token
// in .credentials.json (what actually authenticates and gets billed) and a cached
// `oauthAccount` block in .claude.json (what /status displays). For the default setup
// .claude.json lives at ~/.claude.json (profile root, next to the ~/.claude dir); for a
// CLAUDE_CONFIG_DIR it lives inside that dir. Swapping only the token leaves the CLI
// split-brained: billing one account while showing another.
public sealed class ClaudeAccountSwitchService
{
    private const int BackupsToKeep = 3;
    private const string BackupPrefix = ".credentials.json.aium-backup-";
    private const string ClaudeJsonBackupPrefix = ".claude.json.aium-backup-";

    private static readonly string[] StaleCacheFileNames =
    [
        "ai-usage-monitor-profile.json",
        "ai-usage-monitor-oauth-usage-cache.json",
        "ai-usage-monitor-usage.json",
        "apimonitor-usage.json"
    ];

    private readonly AppSettingsService _settingsService;
    private readonly AppLogService _logService;
    private readonly AnthropicAccountManagerService _accountManager;
    private readonly AnthropicOAuthTokenRefresher _tokenRefresher;
    private readonly string _homeDirectory;

    public ClaudeAccountSwitchService(
        AppSettingsService settingsService,
        AppLogService logService,
        AnthropicAccountManagerService accountManager,
        AnthropicOAuthTokenRefresher? tokenRefresher = null,
        string? homeDirectory = null)
    {
        _settingsService = settingsService;
        _logService = logService;
        _accountManager = accountManager;
        _tokenRefresher = tokenRefresher ?? new AnthropicOAuthTokenRefresher(logService: logService);
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public sealed record SwitchResult(bool Succeeded, string Message, bool AlreadyActive = false);

    public async Task<SwitchResult> SwitchToAsync(ProviderAccount target, CancellationToken cancellationToken)
    {
        if (target.IsDefault)
        {
            return new SwitchResult(false, "The default account is whatever ~/.claude is already logged into; pick a managed account to switch to.");
        }

        var targetCredentialsPath = Path.Combine(target.ConfigDir, ".credentials.json");
        if (string.IsNullOrWhiteSpace(target.ConfigDir) || !File.Exists(targetCredentialsPath))
        {
            return new SwitchResult(false, $"'{target.Label}' has no saved login. Use 'Log in again' first.");
        }

        // Refresh the target's token first so the swapped-in credentials are immediately live.
        var freshToken = await _tokenRefresher
            .GetFreshAccessTokenAsync(targetCredentialsPath, forceRefresh: false, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(freshToken))
        {
            return new SwitchResult(false, $"'{target.Label}' has an expired login. Use 'Log in again', then switch.");
        }

        var claudeDirectory = Path.Combine(_homeDirectory, ".claude");
        var defaultCredentialsPath = Path.Combine(claudeDirectory, ".credentials.json");
        Directory.CreateDirectory(claudeDirectory);

        var settings = _settingsService.Load();

        if (File.Exists(defaultCredentialsPath))
        {
            var activeIdentity = await _accountManager
                .TryFetchIdentityAsync(defaultCredentialsPath, cancellationToken)
                .ConfigureAwait(false);
            var activeUuid = activeIdentity?.Uuid
                ?? settings.ProviderAccounts.FirstOrDefault(account => account.IsDefault &&
                    string.Equals(account.ProviderName, KnownProviders.Anthropic, StringComparison.OrdinalIgnoreCase))?.AccountUuid;

            if (!string.IsNullOrWhiteSpace(activeUuid) &&
                !string.IsNullOrWhiteSpace(target.AccountUuid) &&
                string.Equals(activeUuid, target.AccountUuid, StringComparison.OrdinalIgnoreCase))
            {
                // Even a no-op switch repairs a split-brain: the token may already be this
                // account's while /status still displays a stale cached identity.
                var repaired = await RepairIdentityBlocksAsync(target, defaultCredentialsPath, cancellationToken)
                    .ConfigureAwait(false);
                return new SwitchResult(
                    true,
                    repaired
                        ? $"'{target.Label}' was already the active Claude CLI account, but its cached identity info was stale; repaired it (restart claude sessions to see the right name in /status)."
                        : $"'{target.Label}' is already the active Claude CLI account.",
                    AlreadyActive: true);
            }

            // Sync-back: ~/.claude holds the freshest (possibly rotated) tokens for whichever
            // managed account is currently active, so copy them home before overwriting.
            if (!string.IsNullOrWhiteSpace(activeUuid))
            {
                var activeManagedAccount = settings.ProviderAccounts.FirstOrDefault(account =>
                    !account.IsDefault &&
                    !string.IsNullOrWhiteSpace(account.ConfigDir) &&
                    string.Equals(account.AccountUuid, activeUuid, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(account.Id, target.Id, StringComparison.OrdinalIgnoreCase));
                if (activeManagedAccount is not null)
                {
                    TryCopyCredentials(defaultCredentialsPath, Path.Combine(activeManagedAccount.ConfigDir, ".credentials.json"));

                    // Only sync the identity block back when home's block really belongs to
                    // this account. A stale home block (a previous split-brain) must not be
                    // laundered into the managed dir, where it would poison later switches.
                    if (HomeOAuthBlockMatches(activeManagedAccount.AccountUuid))
                    {
                        TryCopyOAuthAccountBlock(HomeClaudeJsonPath, ManagedClaudeJsonPath(activeManagedAccount.ConfigDir));
                    }
                }
                else if (activeIdentity is not null)
                {
                    // The outgoing login is not stored anywhere we manage: adopt it as its
                    // own account so it stays monitored and can be switched back to later.
                    AdoptOutgoingIdentity(settings, activeIdentity, defaultCredentialsPath);
                    settings = _settingsService.Load();
                }
            }

            try
            {
                var backupPath = Path.Combine(
                    claudeDirectory,
                    $"{BackupPrefix}{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
                File.Copy(defaultCredentialsPath, backupPath, overwrite: true);
                PruneBackups(claudeDirectory, BackupPrefix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SwitchResult(false, $"Could not back up the current ~/.claude credentials: {ex.Message}");
            }
        }

        if (!TryCopyCredentials(targetCredentialsPath, defaultCredentialsPath))
        {
            return new SwitchResult(false, "Could not write the new credentials into ~/.claude.");
        }

        // Swap the cached identity block too or the CLI's /status keeps showing the old
        // account while the new token gets billed. Backed up alongside the credentials.
        try
        {
            if (File.Exists(HomeClaudeJsonPath))
            {
                var identityBackupPath = Path.Combine(
                    claudeDirectory,
                    $"{ClaudeJsonBackupPrefix}{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
                File.Copy(HomeClaudeJsonPath, identityBackupPath, overwrite: true);
                PruneBackups(claudeDirectory, ClaudeJsonBackupPrefix);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logService.Error("Anthropic", $"Could not back up ~/.claude.json before the identity swap: {ex.Message}");
        }

        // Heal a missing or stale identity block in the target dir before installing it:
        // the profile endpoint (queried with the just-swapped-in token) is authoritative.
        var targetClaudeJsonPath = ManagedClaudeJsonPath(target.ConfigDir);
        var targetBlockUuid = TryReadOAuthAccountUuid(targetClaudeJsonPath);
        if (!string.IsNullOrWhiteSpace(target.AccountUuid) &&
            !string.Equals(targetBlockUuid, target.AccountUuid, StringComparison.OrdinalIgnoreCase))
        {
            var rebuiltBlock = await _accountManager
                .TryFetchOAuthAccountBlockAsync(defaultCredentialsPath, cancellationToken)
                .ConfigureAwait(false);
            if (rebuiltBlock is not null)
            {
                TryWriteOAuthAccountBlock(targetClaudeJsonPath, rebuiltBlock);
                _logService.Info(
                    "Anthropic",
                    $"Rebuilt the cached account identity block for '{target.Label}' from the live profile endpoint.");
            }
        }

        if (!TryCopyOAuthAccountBlock(targetClaudeJsonPath, HomeClaudeJsonPath))
        {
            _logService.Error(
                "Anthropic",
                $"'{target.Label}' has no cached oauthAccount block to install; claude /status may show the previous account name until the next login.");
        }

        // Without this the default "Anthropic" card would keep showing the previous
        // account's cached plan/usage for hours.
        foreach (var cacheFileName in StaleCacheFileNames)
        {
            var cachePath = Path.Combine(claudeDirectory, cacheFileName);
            try
            {
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logService.Error("Anthropic", $"Could not delete stale cache {cachePath}: {ex.Message}");
            }
        }

        var defaultAccount = settings.ProviderAccounts.FirstOrDefault(account => account.IsDefault &&
            string.Equals(account.ProviderName, KnownProviders.Anthropic, StringComparison.OrdinalIgnoreCase));
        if (defaultAccount is not null)
        {
            defaultAccount.Email = target.Email;
            defaultAccount.AccountUuid = target.AccountUuid;
            // Any personal name for the outgoing login moved to its adopted entry; the
            // slot itself goes back to being anonymous (the aggregator shows the active
            // managed account's name on the slot card).
            defaultAccount.Label = ProviderAccount.DefaultAccountLabel;
            _settingsService.Save(settings);
        }

        _logService.Info("Anthropic", $"Switched the Claude CLI account in ~/.claude to '{target.Label}'.");
        return new SwitchResult(
            true,
            $"Switched. New claude sessions now use '{target.Label}'. Already-running claude sessions (terminal or the Claude Code desktop app) keep the old account until restarted, or may pick up the new one when their token next refreshes.");
    }

    // Repairs a split-brain for the account that is already active in ~/.claude: makes the
    // cached /status identity agree with what the token actually authenticates as. Safe to
    // call any time; does nothing when everything already matches.
    public async Task<bool> RepairIdentityCacheAsync(ProviderAccount activeAccount, CancellationToken cancellationToken)
    {
        var defaultCredentialsPath = Path.Combine(_homeDirectory, ".claude", ".credentials.json");
        if (!File.Exists(defaultCredentialsPath))
        {
            return false;
        }

        return await RepairIdentityBlocksAsync(activeAccount, defaultCredentialsPath, cancellationToken).ConfigureAwait(false);
    }

    // Makes the cached identity blocks (target dir and home) agree with the account the
    // active token actually belongs to. Returns true when anything was repaired.
    private async Task<bool> RepairIdentityBlocksAsync(
        ProviderAccount target,
        string defaultCredentialsPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.AccountUuid) || string.IsNullOrWhiteSpace(target.ConfigDir))
        {
            return false;
        }

        var repaired = false;
        var targetClaudeJsonPath = ManagedClaudeJsonPath(target.ConfigDir);

        if (!string.Equals(TryReadOAuthAccountUuid(targetClaudeJsonPath), target.AccountUuid, StringComparison.OrdinalIgnoreCase))
        {
            var rebuiltBlock = await _accountManager
                .TryFetchOAuthAccountBlockAsync(defaultCredentialsPath, cancellationToken)
                .ConfigureAwait(false);
            if (rebuiltBlock is not null && TryWriteOAuthAccountBlock(targetClaudeJsonPath, rebuiltBlock))
            {
                repaired = true;
                _logService.Info("Anthropic", $"Rebuilt the cached identity block for '{target.Label}'.");
            }
        }

        if (!HomeOAuthBlockMatches(target.AccountUuid) &&
            TryCopyOAuthAccountBlock(targetClaudeJsonPath, HomeClaudeJsonPath))
        {
            repaired = true;
            _logService.Info("Anthropic", "Repaired the stale cached identity in ~/.claude.json.");
        }

        return repaired;
    }

    private string HomeClaudeJsonPath => Path.Combine(_homeDirectory, ".claude.json");

    private static string ManagedClaudeJsonPath(string configDir) => Path.Combine(configDir, ".claude.json");

    // Copies the `oauthAccount` identity block from one .claude.json into another, leaving
    // every other property of the destination file (projects, tips, onboarding state, ...)
    // untouched. Returns false when the source has no block to copy.
    private bool TryCopyOAuthAccountBlock(string sourceClaudeJsonPath, string destinationClaudeJsonPath)
    {
        try
        {
            if (!File.Exists(sourceClaudeJsonPath))
            {
                return false;
            }

            if (JsonNode.Parse(File.ReadAllText(sourceClaudeJsonPath)) is not JsonObject sourceRoot ||
                sourceRoot["oauthAccount"] is not JsonNode oauthAccount)
            {
                return false;
            }

            return TryWriteOAuthAccountBlock(destinationClaudeJsonPath, oauthAccount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logService.Error(
                "Anthropic",
                $"Could not copy the oauthAccount identity block from {sourceClaudeJsonPath} to {destinationClaudeJsonPath}: {ex.Message}");
            return false;
        }
    }

    private bool TryWriteOAuthAccountBlock(string destinationClaudeJsonPath, JsonNode oauthAccount)
    {
        try
        {
            JsonObject destinationRoot;
            if (File.Exists(destinationClaudeJsonPath) &&
                JsonNode.Parse(File.ReadAllText(destinationClaudeJsonPath)) is JsonObject existingRoot)
            {
                destinationRoot = existingRoot;
            }
            else
            {
                destinationRoot = [];
            }

            destinationRoot["oauthAccount"] = oauthAccount.DeepClone();

            var tempPath = destinationClaudeJsonPath + ".tmp";
            File.WriteAllText(tempPath, destinationRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, destinationClaudeJsonPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logService.Error(
                "Anthropic",
                $"Could not write the oauthAccount identity block to {destinationClaudeJsonPath}: {ex.Message}");
            return false;
        }
    }

    private static string? TryReadOAuthAccountUuid(string claudeJsonPath)
    {
        try
        {
            if (!File.Exists(claudeJsonPath))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(claudeJsonPath)) is JsonObject root
                ? root["oauthAccount"]?["accountUuid"]?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool HomeOAuthBlockMatches(string accountUuid)
    {
        if (string.IsNullOrWhiteSpace(accountUuid))
        {
            return false;
        }

        var homeBlockUuid = TryReadOAuthAccountUuid(HomeClaudeJsonPath);
        return string.Equals(homeBlockUuid, accountUuid, StringComparison.OrdinalIgnoreCase);
    }

    private void AdoptOutgoingIdentity(
        AppSettings settings,
        AnthropicAccountManagerService.AccountIdentity identity,
        string defaultCredentialsPath)
    {
        var defaultAccount = settings.ProviderAccounts.FirstOrDefault(account => account.IsDefault &&
            string.Equals(account.ProviderName, KnownProviders.Anthropic, StringComparison.OrdinalIgnoreCase));
        var defaultLabel = defaultAccount?.Label;
        var label = !string.IsNullOrWhiteSpace(defaultLabel) &&
            !string.Equals(defaultLabel, ProviderAccount.DefaultAccountLabel, StringComparison.OrdinalIgnoreCase)
            ? defaultLabel
            : !string.IsNullOrWhiteSpace(identity.Email) ? identity.Email : "Previous account";

        // The label moves to the adopted entry; clear it off the default row first or the
        // display-key dedupe in Normalize() would rename the adopted account.
        if (defaultAccount is not null &&
            !string.Equals(defaultAccount.Label, ProviderAccount.DefaultAccountLabel, StringComparison.OrdinalIgnoreCase))
        {
            defaultAccount.Label = ProviderAccount.DefaultAccountLabel;
            _settingsService.Save(settings);
        }

        var adopted = _accountManager.CreateAccount(label);
        if (!TryCopyCredentials(defaultCredentialsPath, Path.Combine(adopted.ConfigDir, ".credentials.json")))
        {
            _accountManager.RemoveAccount(adopted.Id);
            return;
        }

        if (HomeOAuthBlockMatches(identity.Uuid))
        {
            TryCopyOAuthAccountBlock(HomeClaudeJsonPath, ManagedClaudeJsonPath(adopted.ConfigDir));
        }

        var reloadedSettings = _settingsService.Load();
        var savedAccount = reloadedSettings.ProviderAccounts.FirstOrDefault(account =>
            string.Equals(account.Id, adopted.Id, StringComparison.OrdinalIgnoreCase));
        if (savedAccount is not null)
        {
            savedAccount.Email = identity.Email;
            savedAccount.AccountUuid = identity.Uuid;
            _settingsService.Save(reloadedSettings);
        }

        _logService.Info("Anthropic", $"Kept the previous CLI login as account '{label}' so it can be switched back to.");
    }

    private bool TryCopyCredentials(string sourcePath, string destinationPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = destinationPath + ".tmp";
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logService.Error("Anthropic", $"Could not copy credentials from {sourcePath} to {destinationPath}: {ex.Message}");
            return false;
        }
    }

    private static void PruneBackups(string claudeDirectory, string prefix)
    {
        var backups = Directory.GetFiles(claudeDirectory, $"{prefix}*")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Skip(BackupsToKeep);

        foreach (var backup in backups)
        {
            try
            {
                File.Delete(backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
