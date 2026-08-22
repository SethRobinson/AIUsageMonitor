using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

// Answers "which Claude account is ~/.claude logged into right now?".
//
// ~/.claude is a SLOT, not an identity. The user can re-login it at any moment from the
// claude CLI, the VS Code extension, or the Claude Code desktop app, and none of those
// tell this app about it. Deciding which card belongs to which account from a uuid cached
// in settings therefore goes wrong the first time the login changes outside the app: the
// slot card keeps the previous account's name while that account's own card shows the same
// numbers, and the account that really moved is never collected at all.
//
// Two sources, in priority order:
//   1. The `oauthAccount` block Claude Code caches in ~/.claude.json. Free, offline, and
//      rewritten by every login, so an external account change is picked up on the very
//      next refresh tick.
//   2. The /api/oauth/profile endpoint keyed by the slot's actual access token. The token
//      is what authenticates and gets billed, so this is the authority, and it settles
//      split-brain cases where the cached block names a different account than the token.
//      The answer is cached per token value, so ordinary refresh ticks cost nothing extra.
public sealed class ClaudeSlotIdentityService
{
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(6);

    private readonly AppLogService _logService;
    private readonly AnthropicAccountManagerService? _accountManager;
    private readonly object _cacheLock = new();
    private string _verifiedTokenFingerprint = string.Empty;
    private SlotIdentity? _verifiedIdentity;
    private DateTimeOffset _verifiedAt = DateTimeOffset.MinValue;

    public ClaudeSlotIdentityService(
        AppLogService logService,
        AnthropicAccountManagerService? accountManager = null,
        string? homeDirectory = null)
    {
        _logService = logService;
        _accountManager = accountManager;
        HomeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // IsVerified means the uuid came from the profile endpoint keyed by the slot's own
    // token, not from a cached block that some other tool may have left behind.
    public sealed record SlotIdentity(string Uuid, string Email, bool IsVerified);

    public string HomeDirectory { get; }

    public string ClaudeDirectory => Path.Combine(HomeDirectory, ".claude");

    public string CredentialsPath => Path.Combine(ClaudeDirectory, ".credentials.json");

    public string ClaudeJsonPath => ClaudeIdentityFiles.ClaudeJsonPathFor(HomeDirectory);

    // False means the slot is logged out entirely; no managed account can be the active one.
    public bool HasLogin => File.Exists(CredentialsPath);

    // Synchronous, no network: safe to call from the collector-resolution path, which runs
    // every time the provider list is rebuilt. Returns the verified identity while it still
    // belongs to the token currently in the slot, otherwise the locally cached block.
    public SlotIdentity? GetIdentity()
    {
        if (!HasLogin)
        {
            return null;
        }

        var fingerprint = ReadTokenFingerprint();
        lock (_cacheLock)
        {
            if (_verifiedIdentity is not null &&
                fingerprint.Length > 0 &&
                string.Equals(_verifiedTokenFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return _verifiedIdentity;
            }
        }

        return ReadCachedBlockIdentity();
    }

    // Confirms the slot identity against the profile endpoint when the token changed (a new
    // login, or a rotation) or the previous confirmation aged out. Never throws for network
    // trouble: an offline tick falls back to the cached block.
    public async Task<SlotIdentity?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!HasLogin)
        {
            lock (_cacheLock)
            {
                _verifiedIdentity = null;
                _verifiedTokenFingerprint = string.Empty;
            }

            return null;
        }

        var blockIdentity = ReadCachedBlockIdentity();
        var fingerprint = ReadTokenFingerprint();

        lock (_cacheLock)
        {
            if (_verifiedIdentity is not null &&
                fingerprint.Length > 0 &&
                string.Equals(_verifiedTokenFingerprint, fingerprint, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - _verifiedAt < VerificationLifetime)
            {
                return _verifiedIdentity;
            }
        }

        if (_accountManager is null)
        {
            return blockIdentity;
        }

        var identity = await _accountManager
            .TryFetchIdentityAsync(CredentialsPath, cancellationToken)
            .ConfigureAwait(false);
        if (identity is null || string.IsNullOrWhiteSpace(identity.Uuid))
        {
            return blockIdentity;
        }

        var verified = new SlotIdentity(identity.Uuid, identity.Email, IsVerified: true);
        lock (_cacheLock)
        {
            _verifiedIdentity = verified;
            _verifiedTokenFingerprint = fingerprint;
            _verifiedAt = DateTimeOffset.UtcNow;
        }

        if (blockIdentity is not null &&
            !string.IsNullOrWhiteSpace(blockIdentity.Uuid) &&
            !string.Equals(blockIdentity.Uuid, verified.Uuid, StringComparison.OrdinalIgnoreCase))
        {
            _logService.Info(
                "Anthropic",
                $"~/.claude.json still names {Describe(blockIdentity)} but the token in ~/.claude authenticates as " +
                $"{Describe(verified)}; the token wins.");
        }

        return verified;
    }

    // While an account is the one logged into ~/.claude, the CLI keeps rotating its tokens
    // there and the managed dir's copy goes stale. Once the slot is logged into a different
    // account the stale copy is all this app has left, and by then it is usually dead: the
    // account's card then shows "login expired" and the user cannot switch back to it.
    // Mirroring the live tokens back on every refresh keeps that account monitorable.
    public bool TryMirrorSlotCredentials(ProviderAccount managedAccount)
    {
        if (managedAccount.IsDefault ||
            string.IsNullOrWhiteSpace(managedAccount.ConfigDir) ||
            string.IsNullOrWhiteSpace(managedAccount.AccountUuid) ||
            !HasLogin)
        {
            return false;
        }

        // Only a token-verified identity may be copied into an account dir. Trusting the
        // cached block here could file one account's tokens under another account's name,
        // which is the very failure this class exists to prevent.
        var identity = GetIdentity();
        if (identity is null ||
            !identity.IsVerified ||
            !string.Equals(identity.Uuid, managedAccount.AccountUuid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var destination = Path.Combine(managedAccount.ConfigDir, ".credentials.json");
        try
        {
            if (File.Exists(destination) &&
                File.GetLastWriteTimeUtc(destination) >= File.GetLastWriteTimeUtc(CredentialsPath))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!ClaudeIdentityFiles.TryCopyCredentials(CredentialsPath, destination, out var error))
        {
            _logService.Error(
                "Anthropic",
                $"Could not mirror the live ~/.claude tokens into '{managedAccount.Label}': {error}");
            return false;
        }

        // Keep the identity block alongside them, but only when the home block really
        // belongs to this account; a split-brain block must not be laundered into the
        // managed dir, where it would poison later switches.
        if (string.Equals(
                ClaudeIdentityFiles.TryReadOAuthAccountUuid(ClaudeJsonPath),
                managedAccount.AccountUuid,
                StringComparison.OrdinalIgnoreCase))
        {
            ClaudeIdentityFiles.TryCopyOAuthAccountBlock(
                ClaudeJsonPath,
                ClaudeIdentityFiles.ClaudeJsonPathFor(managedAccount.ConfigDir),
                out _);
        }

        _logService.Info(
            "Anthropic",
            $"Mirrored the live ~/.claude login into '{managedAccount.Label}' so it stays usable after the CLI switches away.");
        return true;
    }

    private SlotIdentity? ReadCachedBlockIdentity()
    {
        try
        {
            if (!File.Exists(ClaudeJsonPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(ClaudeJsonPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("oauthAccount", out var block) ||
                block.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var uuid = ReadString(block, "accountUuid");
            return string.IsNullOrWhiteSpace(uuid)
                ? null
                : new SlotIdentity(uuid, ReadString(block, "emailAddress"), IsVerified: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // Identifies the token without ever holding or logging it: two different logins produce
    // two different fingerprints, which is all the cache needs to know.
    private string ReadTokenFingerprint()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var accessToken = ReadString(oauth, "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return string.Empty;
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)))[..16];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string Describe(SlotIdentity identity)
    {
        return string.IsNullOrWhiteSpace(identity.Email) ? identity.Uuid : identity.Email;
    }
}
