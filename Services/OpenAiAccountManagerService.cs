using System.Collections.Concurrent;
using System.IO;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class OpenAiAccountManagerService
{
    private readonly AppSettingsService _settings;
    private readonly ICodexAccountClient _client;
    public string AccountsRootDirectory { get; }
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccountLocks = new(StringComparer.OrdinalIgnoreCase);
    internal static SemaphoreSlim Gate(string directory) => AccountLocks.GetOrAdd(Path.GetFullPath(directory), _ => new SemaphoreSlim(1, 1));

    public OpenAiAccountManagerService(AppSettingsService settings, ICodexAccountClient? client = null, string? accountsRootDirectory = null)
    {
        _settings = settings;
        _client = client ?? new CodexAccountClient();
        AccountsRootDirectory = Path.GetFullPath(accountsRootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SethsAIUsageMonitor", "accounts", "openai"));
    }

    public async Task<ProviderAccount> LoginAsync(string label, string? accountId, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new InvalidOperationException("Enter an account label.");
        var existing = accountId is null ? null : Find(accountId) ?? throw new InvalidOperationException("This account was removed. Add it again.");
        var staging = Path.Combine(AccountsRootDirectory, "pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await _client.LoginAsync(staging, openBrowser, timeout.Token).ConfigureAwait(false);
            var snapshot = await _client.ReadAsync(staging, timeout.Token).ConfigureAwait(false);
            var identity = OpenAiIdentityFiles.ReadIdentity(staging);
            if (string.IsNullOrWhiteSpace(identity)) throw new InvalidOperationException("Could not identify this ChatGPT login. Update Codex and try again.");
            if (existing is not null && !string.Equals(existing.AccountUuid, identity, StringComparison.Ordinal))
                throw new InvalidOperationException("You signed into a different OpenAI account. The saved login was kept. Use Add account for a different account.");
            var account = existing?.Clone() ?? new ProviderAccount
            {
                Id = "openai-" + Guid.NewGuid().ToString("N"), ProviderName = KnownProviders.OpenAI,
                Label = label.Trim(), Enabled = true
            };
            account.ConfigDir = existing?.ConfigDir ?? Path.Combine(AccountsRootDirectory, account.Id);
            EnsureOwnedDirectory(account.ConfigDir);
            account.AccountUuid = identity;
            account.Email = snapshot.Email;
            var gate = Gate(account.ConfigDir);
            await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                _settings.Update(settings =>
                {
                    var duplicate = settings.GetAccounts(KnownProviders.OpenAI).FirstOrDefault(other => other.Id != account.Id && other.AccountUuid == identity);
                    if (duplicate is not null) throw new InvalidOperationException($"That OpenAI account is already saved as '{duplicate.Label}'.");
                    var current = settings.ProviderAccounts.Find(other => other.Id == account.Id && other.ProviderName == KnownProviders.OpenAI);
                    if (existing is not null && current is null) throw new InvalidOperationException("This account was removed while login was in progress.");
                    OpenAiIdentityFiles.InstallAuth(staging, account.ConfigDir);
                    if (current is null) settings.ProviderAccounts.Add(account);
                    else { current.Email = account.Email; current.AccountUuid = identity; }
                    settings.OpenAiUseSavedAccounts = true;
                });
            }
            finally { gate.Release(); }
            return account;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("OpenAI login timed out after five minutes. Please try again."); }
        finally { DeleteOwnedDirectory(staging); }
    }

    public void Rename(string accountId, string label)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new InvalidOperationException("Enter an account label.");
        _settings.Update(settings => Require(settings, accountId).Label = label.Trim());
    }

    public void SetEnabled(string accountId, bool enabled) =>
        _settings.Update(settings => Require(settings, accountId).Enabled = enabled);

    public async Task RemoveAsync(string accountId, CancellationToken token = default)
    {
        var account = Find(accountId) ?? throw new InvalidOperationException("This account has already been removed.");
        EnsureOwnedDirectory(account.ConfigDir);
        var gate = Gate(account.ConfigDir);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            DeleteOwnedDirectory(account.ConfigDir);
            _settings.Update(settings => settings.ProviderAccounts.RemoveAll(other => other.Id == accountId && other.ProviderName == KnownProviders.OpenAI));
        }
        finally { gate.Release(); }
    }

    private ProviderAccount? Find(string id) => _settings.Load().GetAccounts(KnownProviders.OpenAI).FirstOrDefault(account => account.Id == id && !account.IsDefault);
    private static ProviderAccount Require(AppSettings settings, string id) => settings.ProviderAccounts.Find(account =>
        account.Id == id && account.ProviderName == KnownProviders.OpenAI && !account.IsDefault) ?? throw new InvalidOperationException("The saved OpenAI account was not found.");

    private void EnsureOwnedDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        // Only immediate children; reject junctions/symlinks and similarly prefixed siblings.
        if (!string.Equals(Path.GetDirectoryName(fullPath), AccountsRootDirectory, StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(AccountsRootDirectory) && (File.GetAttributes(AccountsRootDirectory) & FileAttributes.ReparsePoint) != 0) ||
            (Directory.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("The OpenAI account folder is outside the monitor's managed account directory.");
    }

    private void DeleteOwnedDirectory(string directory)
    {
        EnsureOwnedDirectory(directory);
        if (!Directory.Exists(directory)) return;
        // Do not follow any nested reparse points during recursive deletion.
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The account folder contains a link and could not be removed safely.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
            }
        }
        Directory.Delete(directory, recursive: true);
    }
}
