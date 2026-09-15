using System.Text.Json;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Collectors;

public sealed class OpenAiAccountUsageCollector : IForceRefreshUsageCollector
{
    private readonly ProviderAccount _account;
    private readonly ICodexAccountClient _client;
    private readonly TimeSpan _timeout;
    private DateTimeOffset _retryAt;
    private string _failureMessage = string.Empty;

    public OpenAiAccountUsageCollector(ProviderAccount account, ICodexAccountClient? client = null, TimeSpan? timeout = null)
    { _account = account.Clone(); _client = client ?? new CodexAccountClient(); _timeout = timeout ?? TimeSpan.FromSeconds(45); }

    public string ProviderName => KnownProviders.OpenAI;
    public Task<ProviderUsage> CollectAsync(CancellationToken token) => CollectAsync(false, token);

    public async Task<ProviderUsage> CollectAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _retryAt > DateTimeOffset.UtcNow)
            return ProviderUsageFactory.Unavailable(ProviderName, $"{_failureMessage} Retry after {_retryAt.ToLocalTime():h:mm tt}.", "Codex account");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var gate = OpenAiAccountManagerService.Gate(_account.ConfigDir);
        var acquired = false;
        try
        {
            await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            VerifyIdentity();
            var snapshot = await _client.ReadAsync(_account.ConfigDir, timeout.Token).ConfigureAwait(false);
            VerifyIdentity();
            var result = ParseUsage(snapshot);
            _retryAt = DateTimeOffset.MinValue;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or JsonException)
        {
            _failureMessage = ex is OperationCanceledException ? "OpenAI usage check timed out."
                : ex is InvalidOperationException ? ex.Message : "Could not read the saved OpenAI account. Retry or log in again.";
            _retryAt = DateTimeOffset.UtcNow.AddMinutes(5);
            return ProviderUsageFactory.Unavailable(ProviderName, _failureMessage, "Codex account");
        }
        finally { if (acquired) gate.Release(); }
    }

    private void VerifyIdentity()
    {
        var identity = OpenAiIdentityFiles.ReadIdentity(_account.ConfigDir);
        if (string.IsNullOrWhiteSpace(identity) || !string.Equals(identity, _account.AccountUuid, StringComparison.Ordinal))
            throw new InvalidOperationException("The saved OpenAI login is missing or belongs to a different account. Use Accounts > Log in again.");
    }

    internal static ProviderUsage ParseUsage(CodexAccountSnapshot snapshot)
    {
        var root = snapshot.RateLimits;
        var bucket = default(JsonElement);
        if (root.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object &&
            buckets.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object) bucket = codex;
        else if (root.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object) bucket = legacy;
        var windows = new List<UsageWindow>();
        if (bucket.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                    !window.TryGetProperty("usedPercent", out var usedValue) || usedValue.ValueKind != JsonValueKind.Number ||
                    !usedValue.TryGetDouble(out var used) || !double.IsFinite(used)) continue;
                var title = name == "primary" ? "Primary" : "Secondary";
                if (window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var minutes) && minutes > 0)
                    title = minutes % 1440 == 0 ? $"{minutes / 1440}d" : minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes}m";
                DateTimeOffset? reset = null;
                if (window.TryGetProperty("resetsAt", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var seconds) &&
                    seconds is >= -62135596800 and <= 253402300799) reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                windows.Add(ProviderUsageFactory.PercentWindow(title, used, reset));
            }
        }
        var plan = PlanNameFormatter.Format(CodexAccountClient.JsonString(bucket, "planType") is { Length: > 0 } bucketPlan ? bucketPlan : snapshot.PlanType);
        return windows.Count == 0 ? ProviderUsageFactory.Unavailable(KnownProviders.OpenAI, "No Codex subscription quota windows were returned for this account.", "Codex account", plan)
            : new ProviderUsage { Name = KnownProviders.OpenAI, PlanName = plan, Source = "Codex account", StatusMessage = "Live quota for this saved OpenAI account.", Windows = windows };
    }
}
