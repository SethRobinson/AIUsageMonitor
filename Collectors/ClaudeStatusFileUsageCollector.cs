using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Collectors;

public sealed partial class ClaudeStatusFileUsageCollector : IUsageCollector
{
    private const string ExportName = "ai-usage-monitor-usage.json";
    private const string LegacyExportName = "apimonitor-usage.json";
    private const string ExporterScriptName = "ai-usage-monitor-statusline.ps1";
    private static readonly TimeSpan LocalExportMaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PassedResetStaleGrace = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CommandSuccessCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CommandFailureBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandUnavailableBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan OAuthRateLimitBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UnknownExhaustionBackoff = TimeSpan.FromMinutes(30);
    private DateTimeOffset _nextCommandRefreshAt = DateTimeOffset.MinValue;
    private string _commandRefreshPauseMessage = string.Empty;
    private DateTimeOffset _nextOAuthUsageAt = DateTimeOffset.MinValue;
    private string _oauthUsagePauseMessage = string.Empty;

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly string? _homeDirectory;
    private readonly HttpClient _httpClient;

    public ClaudeStatusFileUsageCollector(string? homeDirectory = null, HttpClient? httpClient = null)
    {
        _homeDirectory = homeDirectory;
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public string ProviderName => KnownProviders.Anthropic;

    public async Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
    {
        var home = _homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeDirectory = Path.Combine(home, ".claude");
        var credentialsPath = Path.Combine(claudeDirectory, ".credentials.json");
        var account = TryReadClaudeAccount(credentialsPath);
        var planName = account?.PlanName ?? string.Empty;
        var candidatePaths = new[]
        {
            Path.Combine(claudeDirectory, ExportName),
            Path.Combine(claudeDirectory, LegacyExportName),
            Path.Combine(claudeDirectory, "usage-status.json"),
            Path.Combine(claudeDirectory, "usage-status.md")
        };

        var localUsage = TryReadLocalUsage(candidatePaths, planName, cancellationToken);

        if (localUsage.FreshUsage is not null)
        {
            return localUsage.FreshUsage;
        }

        var statusNotes = new List<string>();
        var now = DateTimeOffset.Now;
        var oauthPaused = _nextOAuthUsageAt > now;
        var oauthUsage = OAuthUsageReadResult.None;

        if (oauthPaused)
        {
            AddStatusNote(statusNotes, $"{_oauthUsagePauseMessage} Next live usage retry after {_nextOAuthUsageAt.ToLocalTime():h:mm tt}.");
        }
        else
        {
            oauthUsage = await TryCollectOAuthUsageAsync(claudeDirectory, account, cancellationToken);
            if (oauthUsage.Usage is not null && !oauthUsage.Usage.IsUnavailable)
            {
                return oauthUsage.Usage;
            }

            if (oauthUsage.IsRateLimited)
            {
                var retryAt = now.Add(OAuthRateLimitBackoff);
                var rateLimitMessage = oauthUsage.Usage?.StatusMessage ?? "Claude live usage endpoint is temporarily rate-limited.";
                PauseOAuthUsage(retryAt, rateLimitMessage);
                AddStatusNote(statusNotes, $"{rateLimitMessage} Next live usage retry after {retryAt.ToLocalTime():h:mm tt}.");
            }
        }

        if (_nextCommandRefreshAt <= now && !oauthPaused && !oauthUsage.IsRateLimited)
        {
            var refreshResult = await CliQuotaRefreshRunner.RefreshClaudeAsync(cancellationToken);
            var refreshedLocalUsage = TryReadLocalUsage(candidatePaths, planName, cancellationToken);
            now = DateTimeOffset.Now;

            if (refreshResult.Succeeded)
            {
                _nextCommandRefreshAt = now.Add(CommandSuccessCooldown);
                _commandRefreshPauseMessage = string.Empty;

                if (refreshedLocalUsage.FreshUsage is not null)
                {
                    return WithStatusNote(refreshedLocalUsage.FreshUsage, "Refreshed by Claude command.");
                }

                AddStatusNote(statusNotes, "Claude refresh command ran, but no fresh status export was written.");
                localUsage = refreshedLocalUsage;
            }
            else
            {
                var retryAt = refreshResult.IsQuotaExhausted
                    ? GetExhaustionRetryAt(localUsage.StaleUsage, now) ?? now.Add(UnknownExhaustionBackoff)
                    : now.Add(refreshResult.CommandFound ? CommandFailureBackoff : CommandUnavailableBackoff);

                PauseCommandRefresh(retryAt, refreshResult.Message);
                AddStatusNote(statusNotes, $"{refreshResult.Message} Next command retry after {retryAt.ToLocalTime():h:mm tt}.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(_commandRefreshPauseMessage))
        {
            AddStatusNote(statusNotes, $"{_commandRefreshPauseMessage} Next command retry after {_nextCommandRefreshAt.ToLocalTime():h:mm tt}.");
        }

        if (localUsage.StaleExport is not null)
        {
            var staleMessage = $"{FormatStaleExportMessage(localUsage.StaleExport)} Start Claude Code and send one prompt, or wait for OAuth usage collection to recover.";
            var refreshNote = string.Join(" ", statusNotes);
            if (!string.IsNullOrWhiteSpace(refreshNote))
            {
                staleMessage += " " + refreshNote;
            }

            return ProviderUsageFactory.Unavailable(
                ProviderName,
                staleMessage,
                localUsage.StaleExport.Path,
                planName);
        }

        if (oauthUsage.Usage is not null)
        {
            return WithStatusNote(oauthUsage.Usage, string.Join(" ", statusNotes));
        }

        var exporterPath = Path.Combine(claudeDirectory, ExporterScriptName);
        var message = File.Exists(exporterPath)
            ? "Claude status exporter is installed, but no usage export exists yet. Start Claude Code interactively and send one prompt so the status line receives rate_limits."
            : $"No Claude quota status file found. Configure Claude Code status-line/proxy output to write ~/.claude/{ExportName}.";

        var finalRefreshNote = string.Join(" ", statusNotes);
        if (!string.IsNullOrWhiteSpace(finalRefreshNote))
        {
            message += " " + finalRefreshNote;
        }

        return ProviderUsageFactory.Unavailable(
            ProviderName,
            message,
            claudeDirectory,
            planName);
    }

    private static LocalUsageReadResult TryReadLocalUsage(
        IReadOnlyList<string> candidatePaths,
        string planName,
        CancellationToken cancellationToken)
    {
        ProviderUsage? freshLocalUsage = null;
        ProviderUsage? staleLocalUsage = null;
        StaleLocalExport? staleLocalExport = null;
        var now = DateTimeOffset.Now;

        foreach (var path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            var usage = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? TryParseJson(text, path, planName)
                : TryParseText(text, path, planName);

            if (!IsFreshLocalExport(path, text, out var exportUpdatedAt))
            {
                TrackStaleLocalExport(
                    path,
                    exportUpdatedAt,
                    StaleLocalExportReason.FileTooOld,
                    usage,
                    ref staleLocalUsage,
                    ref staleLocalExport);

                continue;
            }

            if (usage is not null &&
                !usage.IsUnavailable &&
                HasPassedResetWindow(usage, now))
            {
                TrackStaleLocalExport(
                    path,
                    exportUpdatedAt,
                    StaleLocalExportReason.PassedResetWindow,
                    usage,
                    ref staleLocalUsage,
                    ref staleLocalExport);

                continue;
            }

            if (usage is not null && !usage.IsUnavailable)
            {
                freshLocalUsage = usage;
                break;
            }
        }

        return new LocalUsageReadResult(freshLocalUsage, staleLocalUsage, staleLocalExport);
    }

    private static void TrackStaleLocalExport(
        string path,
        DateTimeOffset exportUpdatedAt,
        StaleLocalExportReason reason,
        ProviderUsage? usage,
        ref ProviderUsage? staleLocalUsage,
        ref StaleLocalExport? staleLocalExport)
    {
        if (staleLocalExport is null || exportUpdatedAt > staleLocalExport.UpdatedAt)
        {
            staleLocalExport = new StaleLocalExport(path, exportUpdatedAt, reason);
            staleLocalUsage = usage is not null && !usage.IsUnavailable ? usage : staleLocalUsage;
        }
    }

    private static ProviderUsage? TryParseJson(string text, string path, string fallbackPlanName)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var planName = TryGetClaudePlanName(root, fallbackPlanName);
            var windows = new List<UsageWindow>();

            if (root.TryGetProperty("rate_limits", out var rateLimits))
            {
                AddClaudeWindow(windows, rateLimits, "five_hour", "5h");
                AddClaudeWindow(windows, rateLimits, "seven_day", "7d");
            }
            else
            {
                AddClaudeWindow(windows, root, "five_hour", "5h");
                AddClaudeWindow(windows, root, "seven_day", "7d");
            }

            if (windows.Count == 0)
            {
                var statusMessage = root.TryGetProperty("statusMessage", out var statusMessageElement)
                    ? statusMessageElement.GetString()
                    : null;

                return ProviderUsageFactory.Unavailable(
                    "Anthropic",
                    string.IsNullOrWhiteSpace(statusMessage) ? "Claude status export did not contain rate limit windows yet." : statusMessage,
                    path,
                    planName);
            }

            return new ProviderUsage
            {
                Name = "Anthropic",
                PlanName = planName,
                Source = path,
                StatusMessage = "Claude quota from local status output.",
                Windows = windows
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsFreshLocalExport(string path, string text, out DateTimeOffset exportUpdatedAt)
    {
        exportUpdatedAt = File.GetLastWriteTime(path);

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("generatedAt", out var generatedAtElement) &&
                    generatedAtElement.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(generatedAtElement.GetString(), out var generatedAt))
                {
                    exportUpdatedAt = generatedAt;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
            }
        }

        return DateTimeOffset.Now - exportUpdatedAt.ToLocalTime() <= LocalExportMaxAge;
    }

    private static string FormatRelativeAge(DateTimeOffset updatedAt)
    {
        var elapsed = DateTimeOffset.Now - updatedAt.ToLocalTime();
        if (elapsed.TotalMinutes < 90)
        {
            return $"{Math.Max(1, (int)Math.Round(elapsed.TotalMinutes))} minutes ago";
        }

        if (elapsed.TotalHours < 36)
        {
            return $"{Math.Max(1, (int)Math.Round(elapsed.TotalHours))} hours ago";
        }

        return $"{Math.Max(1, (int)Math.Round(elapsed.TotalDays))} days ago";
    }

    private static string FormatStaleExportMessage(StaleLocalExport staleExport)
    {
        return staleExport.Reason switch
        {
            StaleLocalExportReason.PassedResetWindow =>
                $"Claude status export has stale quota data; it contains a reset timestamp that already passed even though the file was updated {FormatRelativeAge(staleExport.UpdatedAt)}.",
            _ =>
                $"Claude status export is stale; last update was {FormatRelativeAge(staleExport.UpdatedAt)}."
        };
    }

    private async Task<OAuthUsageReadResult> TryCollectOAuthUsageAsync(
        string claudeDirectory,
        ClaudeAccount? account,
        CancellationToken cancellationToken)
    {
        var credentialsPath = Path.Combine(claudeDirectory, ".credentials.json");
        if (!File.Exists(credentialsPath))
        {
            return OAuthUsageReadResult.None;
        }

        account ??= TryReadClaudeAccount(credentialsPath);
        var accessToken = account?.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return OAuthUsageReadResult.None;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OAuthUsageReadResult.Unavailable(ProviderUsageFactory.Unavailable(
                "Anthropic",
                "Claude OAuth usage endpoint timed out. Local status-line export will be used if it is fresh.",
                credentialsPath,
                account?.PlanName ?? string.Empty));
        }
        catch (HttpRequestException ex)
        {
            return OAuthUsageReadResult.Unavailable(ProviderUsageFactory.Unavailable(
                "Anthropic",
                $"Claude OAuth usage endpoint failed: {ex.Message}",
                credentialsPath,
                account?.PlanName ?? string.Empty));
        }

        using (response)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return OAuthUsageReadResult.Unavailable(ProviderUsageFactory.Unavailable(
                    "Anthropic",
                    "Claude OAuth usage endpoint rejected the cached Claude Code credentials. Local status-line export is preferred; start Claude Code once if no status export exists yet.",
                    credentialsPath,
                    account?.PlanName ?? string.Empty));
            }

            if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests)
            {
                return OAuthUsageReadResult.RateLimited(ProviderUsageFactory.Unavailable(
                    "Anthropic",
                    "Claude live usage endpoint is temporarily rate-limited. Local status-line export will be used if available.",
                    credentialsPath,
                    account?.PlanName ?? string.Empty));
            }

            if (!response.IsSuccessStatusCode)
            {
                return OAuthUsageReadResult.Unavailable(ProviderUsageFactory.Unavailable(
                    "Anthropic",
                    $"Claude OAuth usage endpoint returned {FormatStatusCode(response)}. Local status-line export will be used if it is fresh.",
                    credentialsPath,
                    account?.PlanName ?? string.Empty));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var windows = new List<UsageWindow>();
            try
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;

                AddOAuthLimitWindows(windows, root);
                AddOAuthWindowIfMissing(windows, root, "five_hour", "5h");
                AddOAuthWindowIfMissing(windows, root, "seven_day", "7d");
                AddOAuthWindowIfMissing(windows, root, "seven_day_sonnet", "Sonnet");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return OAuthUsageReadResult.Unavailable(ProviderUsageFactory.Unavailable(
                    "Anthropic",
                    $"Claude OAuth usage endpoint returned an unexpected response shape: {ex.Message}. Local status-line export will be used if it is fresh.",
                    credentialsPath,
                    account?.PlanName ?? string.Empty));
            }

            if (windows.Count == 0)
            {
                return OAuthUsageReadResult.None;
            }

            return OAuthUsageReadResult.Available(new ProviderUsage
            {
                Name = "Anthropic",
                PlanName = account?.PlanName ?? string.Empty,
                Source = "Claude OAuth usage endpoint",
                StatusMessage = string.IsNullOrWhiteSpace(account?.PlanName)
                    ? "Claude quota from local Claude Code OAuth credentials."
                    : $"Claude {account.PlanName} quota from local Claude Code OAuth credentials.",
                Windows = windows
            });
        }
    }

    private static string FormatStatusCode(HttpResponseMessage response)
    {
        var status = $"{(int)response.StatusCode} {response.StatusCode}";
        return string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? status
            : $"{status} ({response.ReasonPhrase})";
    }

    private static ClaudeAccount? TryReadClaudeAccount(string credentialsPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(credentialsPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var subscriptionType = TryFindStringProperty(root, "subscriptionType");
            var rateLimitTier = TryFindStringProperty(root, "rateLimitTier");
            var planName = PlanNameFormatter.FormatClaude(subscriptionType, rateLimitTier);

            if (root.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.ValueKind == JsonValueKind.Object &&
                oauth.TryGetProperty("accessToken", out var tokenElement))
            {
                return new ClaudeAccount(tokenElement.GetString(), planName);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static void AddOAuthWindow(List<UsageWindow> windows, JsonElement root, string propertyName, string title)
    {
        if (!root.TryGetProperty(propertyName, out var window) ||
            window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (!TryGetDouble(window, "utilization", out var usedPercent) &&
            !TryGetDouble(window, "used_percentage", out usedPercent))
        {
            return;
        }

        var resetAt = TryGetResetAt(window);

        windows.Add(ProviderUsageFactory.PercentWindow(title, usedPercent, NormalizeResetAt(usedPercent, resetAt)));
    }

    private static void AddOAuthWindowIfMissing(List<UsageWindow> windows, JsonElement root, string propertyName, string title)
    {
        if (windows.Any(window => string.Equals(window.Title, title, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        AddOAuthWindow(windows, root, propertyName, title);
    }

    private static void AddOAuthLimitWindows(List<UsageWindow> windows, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("limits", out var limits) ||
            limits.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object ||
                !TryGetDouble(limit, "percent", out var usedPercent))
            {
                continue;
            }

            var title = GetOAuthLimitTitle(limit);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var resetAt = TryGetResetAt(limit);
            windows.Add(ProviderUsageFactory.PercentWindow(title, usedPercent, NormalizeResetAt(usedPercent, resetAt)));
        }
    }

    private static string GetOAuthLimitTitle(JsonElement limit)
    {
        var kind = TryGetString(limit, "kind");
        var group = TryGetString(limit, "group");
        if (string.Equals(kind, "session", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(group, "session", StringComparison.OrdinalIgnoreCase))
        {
            return "5h";
        }

        if (string.Equals(kind, "weekly_all", StringComparison.OrdinalIgnoreCase))
        {
            return "7d";
        }

        if (string.Equals(kind, "weekly_scoped", StringComparison.OrdinalIgnoreCase))
        {
            var modelName = limit.TryGetProperty("scope", out var scope)
                ? TryFindStringProperty(scope, "display_name")
                : null;
            return PlanNameFormatter.Format(modelName);
        }

        return string.Empty;
    }

    private static void AddClaudeWindow(List<UsageWindow> windows, JsonElement container, string propertyName, string title)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!TryGetDouble(window, "used_percentage", out var usedPercent) &&
            !TryGetDouble(window, "used_percent", out usedPercent))
        {
            return;
        }

        var resetAt = TryGetResetAt(window);

        windows.Add(ProviderUsageFactory.PercentWindow(title, usedPercent, resetAt));
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(property.GetString(), out value),
            _ => false
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static DateTimeOffset? TryGetResetAt(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            (!element.TryGetProperty("resets_at", out var resetElement) &&
            !element.TryGetProperty("reset_at", out resetElement)))
        {
            return null;
        }

        return resetElement.ValueKind switch
        {
            JsonValueKind.Number => DateTimeOffset.FromUnixTimeSeconds(resetElement.GetInt64()),
            JsonValueKind.String when DateTimeOffset.TryParse(resetElement.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? NormalizeResetAt(double usedPercent, DateTimeOffset? resetAt)
    {
        if (usedPercent <= 0.1 &&
            resetAt is { } resetAtValue &&
            resetAtValue <= DateTimeOffset.Now.AddMinutes(-1))
        {
            return null;
        }

        return resetAt;
    }

    private static bool HasPassedResetWindow(ProviderUsage usage, DateTimeOffset now)
    {
        return usage.Windows.Any(window =>
            !window.IsInactive &&
            window.ResetAt is { } resetAt &&
            resetAt <= now.Subtract(PassedResetStaleGrace));
    }

    private static ProviderUsage? TryParseText(string text, string path, string planName)
    {
        var windows = new List<UsageWindow>();
        var fiveHourMatch = FiveHourRegex().Match(text);
        var sevenDayMatch = SevenDayRegex().Match(text);

        if (fiveHourMatch.Success && double.TryParse(fiveHourMatch.Groups["used"].Value, out var fiveHourUsed))
        {
            windows.Add(ProviderUsageFactory.PercentWindow("5h", fiveHourUsed, null));
        }

        if (sevenDayMatch.Success && double.TryParse(sevenDayMatch.Groups["used"].Value, out var sevenDayUsed))
        {
            windows.Add(ProviderUsageFactory.PercentWindow("7d", sevenDayUsed, null));
        }

        if (windows.Count == 0)
        {
            return null;
        }

        return new ProviderUsage
        {
            Name = "Anthropic",
            PlanName = planName,
            Source = path,
            StatusMessage = "Claude quota from local text status output.",
            Windows = windows
        };
    }

    private static string TryGetClaudePlanName(JsonElement root, string fallbackPlanName)
    {
        var subscriptionType = TryFindStringProperty(root, "subscriptionType");
        var rateLimitTier = TryFindStringProperty(root, "rateLimitTier");
        var planName = PlanNameFormatter.FormatClaude(subscriptionType, rateLimitTier);
        if (!string.IsNullOrWhiteSpace(planName))
        {
            return planName;
        }

        return PlanNameFormatter.Format(
            TryFindStringProperty(root, "planName") ??
            TryFindStringProperty(root, "plan")) is { Length: > 0 } formatted
                ? formatted
                : fallbackPlanName;
    }

    private static string? TryFindStringProperty(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }

                    var nested = TryFindStringProperty(property.Value, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = TryFindStringProperty(item, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }

                break;
        }

        return null;
    }

    private static DateTimeOffset? GetExhaustionRetryAt(ProviderUsage? usage, DateTimeOffset now)
    {
        if (usage is null)
        {
            return null;
        }

        var retryAt = usage.Windows
            .Where(window => window.RemainingPercent <= 0.1 && window.ResetAt is not null && window.ResetAt.Value.ToLocalTime() > now)
            .Select(window => window.ResetAt!.Value.ToLocalTime())
            .DefaultIfEmpty()
            .Max();

        return retryAt == default ? null : retryAt;
    }

    private static ProviderUsage WithStatusNote(ProviderUsage usage, string note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return usage;
        }

        return new ProviderUsage
        {
            Name = usage.Name,
            PlanName = usage.PlanName,
            Source = usage.Source,
            StatusMessage = usage.StatusMessage + " " + note,
            IsUnavailable = usage.IsUnavailable,
            LastCheckedAt = usage.LastCheckedAt,
            Windows = usage.Windows
        };
    }

    private void PauseCommandRefresh(DateTimeOffset retryAt, string message)
    {
        _nextCommandRefreshAt = retryAt <= DateTimeOffset.Now
            ? DateTimeOffset.Now.Add(CommandFailureBackoff)
            : retryAt;
        _commandRefreshPauseMessage = message;
    }

    private void PauseOAuthUsage(DateTimeOffset retryAt, string message)
    {
        _nextOAuthUsageAt = retryAt <= DateTimeOffset.Now
            ? DateTimeOffset.Now.Add(OAuthRateLimitBackoff)
            : retryAt;
        _oauthUsagePauseMessage = message;
    }

    private static void AddStatusNote(List<string> notes, string note)
    {
        if (!string.IsNullOrWhiteSpace(note))
        {
            notes.Add(note);
        }
    }

    private sealed record LocalUsageReadResult(
        ProviderUsage? FreshUsage,
        ProviderUsage? StaleUsage,
        StaleLocalExport? StaleExport);

    private sealed record OAuthUsageReadResult(ProviderUsage? Usage, bool IsRateLimited)
    {
        public static readonly OAuthUsageReadResult None = new(null, false);

        public static OAuthUsageReadResult Available(ProviderUsage usage) => new(usage, false);

        public static OAuthUsageReadResult Unavailable(ProviderUsage usage) => new(usage, false);

        public static OAuthUsageReadResult RateLimited(ProviderUsage usage) => new(usage, true);
    }

    private sealed record ClaudeAccount(string? AccessToken, string PlanName);

    private enum StaleLocalExportReason
    {
        FileTooOld,
        PassedResetWindow
    }

    private sealed record StaleLocalExport(string Path, DateTimeOffset UpdatedAt, StaleLocalExportReason Reason);

    [GeneratedRegex(@"5h\s*=\s*(?<used>\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase)]
    private static partial Regex FiveHourRegex();

    [GeneratedRegex(@"7d\s*=\s*(?<used>\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase)]
    private static partial Regex SevenDayRegex();
}
