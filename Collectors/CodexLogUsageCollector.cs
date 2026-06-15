using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Collectors;

public sealed class CodexLogUsageCollector : IUsageCollector
{
    private static readonly TimeSpan FreshSnapshotMaxAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CommandFailureBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandUnavailableBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan UnknownExhaustionBackoff = TimeSpan.FromMinutes(30);
    private readonly AppLogService? _logService;
    private readonly AppSettingsService? _settingsService;
    private DateTimeOffset _nextCommandRefreshAt = DateTimeOffset.MinValue;
    private string _commandRefreshPauseMessage = string.Empty;

    public CodexLogUsageCollector(AppLogService? logService = null, AppSettingsService? settingsService = null)
    {
        _logService = logService;
        _settingsService = settingsService;
    }

    public string ProviderName => KnownProviders.OpenAI;

    public async Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
    {
        var diagnosticsEnabled = IsDiagnosticLoggingEnabled();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sessionsDirectory = Path.Combine(home, ".codex", "sessions");

        if (diagnosticsEnabled)
        {
            LogDiagnostic(BuildEnvironmentDiagnostic(sessionsDirectory));
        }

        var latest = TryReadLatestSnapshot(sessionsDirectory, cancellationToken);
        var now = DateTimeOffset.Now;
        var refreshNote = string.Empty;

        if (GetExhaustionRetryAt(latest, now) is { } exhaustionRetryAt)
        {
            refreshNote = $"Codex quota appears exhausted; refresh command paused until {exhaustionRetryAt.ToLocalTime():MMM d, h:mm tt}.";
            PauseCommandRefresh(exhaustionRetryAt, refreshNote);
        }
        else if (ShouldRunCommandRefresh(latest, now))
        {
            var previousTimestamp = latest?.Timestamp;
            var refreshResult = await CliQuotaRefreshRunner.RefreshCodexAsync(
                cancellationToken,
                diagnosticsEnabled ? LogDiagnostic : null);
            latest = TryReadLatestSnapshot(sessionsDirectory, cancellationToken);
            now = DateTimeOffset.Now;

            if (refreshResult.Succeeded)
            {
                _nextCommandRefreshAt = now.Add(FreshSnapshotMaxAge);
                _commandRefreshPauseMessage = string.Empty;

                if (latest is null || previousTimestamp is not null && latest.Timestamp <= previousTimestamp.Value)
                {
                    refreshNote = "Codex refresh command ran, but no newer quota snapshot was written.";
                }
            }
            else
            {
                var retryAt = refreshResult.IsQuotaExhausted
                    ? GetExhaustionRetryAt(latest, now) ?? now.Add(UnknownExhaustionBackoff)
                    : now.Add(refreshResult.CommandFound ? CommandFailureBackoff : CommandUnavailableBackoff);

                PauseCommandRefresh(retryAt, refreshResult.Message);
                refreshNote = $"{refreshResult.Message} Next command retry after {retryAt.ToLocalTime():h:mm tt}.";
            }
        }
        else if (_nextCommandRefreshAt > now && !string.IsNullOrWhiteSpace(_commandRefreshPauseMessage))
        {
            refreshNote = $"{_commandRefreshPauseMessage} Next command retry after {_nextCommandRefreshAt.ToLocalTime():h:mm tt}.";
        }

        if (diagnosticsEnabled)
        {
            LogDiagnostic(BuildSnapshotDiagnostic(latest, now, refreshNote));
        }

        if (latest is null)
        {
            var message = Directory.Exists(sessionsDirectory)
                ? "No Codex quota snapshots were found in local session logs."
                : "No Codex session directory found.";

            if (!string.IsNullOrWhiteSpace(refreshNote))
            {
                message += " " + refreshNote;
            }

            return ProviderUsageFactory.Unavailable(ProviderName, message, sessionsDirectory);
        }

        return BuildProviderUsage(latest, refreshNote);
    }

    private static CodexRateLimitSnapshot? TryReadLatestSnapshot(string sessionsDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sessionsDirectory))
        {
            return null;
        }

        CodexRateLimitSnapshot? latest = null;

        foreach (var file in EnumerateRecentJsonlFiles(sessionsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var line in ReadLinesLenient(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = TryParseRateLimitLine(line);

                if (snapshot is null)
                {
                    continue;
                }

                if (latest is null || snapshot.Timestamp > latest.Timestamp)
                {
                    latest = snapshot with { SourceFile = file };
                }
            }
        }

        return latest;
    }

    private ProviderUsage BuildProviderUsage(CodexRateLimitSnapshot latest, string refreshNote)
    {
        var windows = new List<UsageWindow>();

        if (latest.Primary is not null)
        {
            windows.Add(ProviderUsageFactory.PercentWindow(
                WindowTitle(latest.Primary.WindowMinutes),
                latest.Primary.UsedPercent,
                latest.Primary.ResetsAt));
        }

        if (latest.Secondary is not null)
        {
            windows.Add(ProviderUsageFactory.PercentWindow(
                WindowTitle(latest.Secondary.WindowMinutes),
                latest.Secondary.UsedPercent,
                latest.Secondary.ResetsAt));
        }

        var planName = PlanNameFormatter.Format(latest.PlanType);
        var statusMessage = string.IsNullOrWhiteSpace(planName)
            ? "Codex quota from latest local token-count event."
            : $"Codex {planName} quota from latest local token-count event.";

        if (!string.IsNullOrWhiteSpace(refreshNote))
        {
            statusMessage += " " + refreshNote;
        }

        return new ProviderUsage
        {
            Name = ProviderName,
            PlanName = planName,
            Source = "Codex local session logs",
            StatusMessage = statusMessage,
            Windows = windows
        };
    }

    private static IEnumerable<string> EnumerateRecentJsonlFiles(string sessionsDirectory)
    {
        return Directory.EnumerateFiles(sessionsDirectory, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(40)
            .Select(file => file.FullName);
    }

    private static IEnumerable<string> ReadLinesLenient(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();

            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static CodexRateLimitSnapshot? TryParseRateLimitLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("rate_limits", out var rateLimits) ||
                rateLimits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var timestamp = root.TryGetProperty("timestamp", out var timestampElement) &&
                            timestampElement.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(timestampElement.GetString(), out var parsedTimestamp)
                ? parsedTimestamp
                : DateTimeOffset.MinValue;

            var limitId = rateLimits.TryGetProperty("limit_id", out var limitIdElement) &&
                          limitIdElement.ValueKind == JsonValueKind.String
                ? limitIdElement.GetString()
                : null;

            if (!string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new CodexRateLimitSnapshot(
                timestamp,
                ParseWindow(rateLimits, "primary"),
                ParseWindow(rateLimits, "secondary"),
                TryGetString(rateLimits, "plan_type"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static CodexWindow? ParseWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedPercent = TryGetDouble(window, "used_percent") ?? 0;
        var windowMinutes = TryGetInt32(window, "window_minutes") ?? 0;
        var resetsAt = TryGetUnixSeconds(window, "resets_at");

        if (usedPercent <= 0 && windowMinutes <= 0 && resetsAt is null)
        {
            return null;
        }

        return new CodexWindow(usedPercent, windowMinutes, resetsAt);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static DateTimeOffset? TryGetUnixSeconds(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(property.GetString(), out unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;
    }

    private static string WindowTitle(int windowMinutes)
    {
        return windowMinutes switch
        {
            300 => "5h",
            10080 => "7d",
            >= 1440 when windowMinutes % 1440 == 0 => $"{windowMinutes / 1440}d",
            >= 60 when windowMinutes % 60 == 0 => $"{windowMinutes / 60}h",
            > 0 => $"{windowMinutes}m",
            _ => "Usage"
        };
    }

    private bool ShouldRunCommandRefresh(CodexRateLimitSnapshot? latest, DateTimeOffset now)
    {
        if (_nextCommandRefreshAt > now)
        {
            return false;
        }

        return latest is null ||
            now - latest.Timestamp.ToLocalTime() > FreshSnapshotMaxAge ||
            EnumerateWindows(latest).Any(window => window.ResetsAt is { } resetAt && resetAt.ToLocalTime() <= now.AddMinutes(-1));
    }

    private static DateTimeOffset? GetExhaustionRetryAt(CodexRateLimitSnapshot? latest, DateTimeOffset now)
    {
        if (latest is null)
        {
            return null;
        }

        var retryAt = EnumerateWindows(latest)
            .Where(window => window.UsedPercent >= 99.9 && window.ResetsAt is not null && window.ResetsAt.Value.ToLocalTime() > now)
            .Select(window => window.ResetsAt!.Value.ToLocalTime())
            .DefaultIfEmpty()
            .Max();

        return retryAt == default ? null : retryAt;
    }

    private static IEnumerable<CodexWindow> EnumerateWindows(CodexRateLimitSnapshot snapshot)
    {
        if (snapshot.Primary is not null)
        {
            yield return snapshot.Primary;
        }

        if (snapshot.Secondary is not null)
        {
            yield return snapshot.Secondary;
        }
    }

    private void PauseCommandRefresh(DateTimeOffset retryAt, string message)
    {
        _nextCommandRefreshAt = retryAt <= DateTimeOffset.Now
            ? DateTimeOffset.Now.Add(CommandFailureBackoff)
            : retryAt;
        _commandRefreshPauseMessage = message;
    }

    private bool IsDiagnosticLoggingEnabled()
    {
        if (_logService is null || _settingsService is null)
        {
            return false;
        }

        try
        {
            return _settingsService.Load().DiagnosticLoggingEnabled;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void LogDiagnostic(string message)
    {
        try
        {
            _logService?.Error("Codex Diagnostics", message);
        }
        catch (Exception)
        {
            // Diagnostic logging must never break usage collection.
        }
    }

    private static string BuildEnvironmentDiagnostic(string sessionsDirectory)
    {
        return $"env: culture={CultureInfo.CurrentCulture.Name}, uiCulture={CultureInfo.CurrentUICulture.Name}, " +
            $"consoleOut={DescribeEncoding(Console.OutputEncoding)}, consoleIn={DescribeEncoding(Console.InputEncoding)}, " +
            $"timeZone={TimeZoneInfo.Local.Id} (UTC{DateTimeOffset.Now:zzz}), " +
            $"sessionsDir={sessionsDirectory}, dirExists={Directory.Exists(sessionsDirectory)}.";
    }

    private static string DescribeEncoding(Encoding encoding)
    {
        return $"{encoding.WebName} (cp{encoding.CodePage})";
    }

    private string BuildSnapshotDiagnostic(CodexRateLimitSnapshot? latest, DateTimeOffset now, string refreshNote)
    {
        var builder = new StringBuilder();

        if (latest is null)
        {
            builder.AppendLine("snapshot: none selected.");
        }
        else
        {
            var ageMinutes = (now - latest.Timestamp).TotalMinutes;
            builder.AppendLine(
                $"snapshot: timestampUtc={latest.Timestamp.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}, " +
                $"ageMinutes={ageMinutes:0.0}, plan={latest.PlanType ?? "(none)"}, file={latest.SourceFile ?? "(unknown)"}.");
            AppendWindowDiagnostic(builder, "primary", latest.Primary, now);
            AppendWindowDiagnostic(builder, "secondary", latest.Secondary, now);
        }

        builder.Append(
            $"refreshNote: {(string.IsNullOrWhiteSpace(refreshNote) ? "(none)" : refreshNote)}; " +
            $"nextCommandRefreshAt={_nextCommandRefreshAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}.");
        return builder.ToString();
    }

    private static void AppendWindowDiagnostic(StringBuilder builder, string label, CodexWindow? window, DateTimeOffset now)
    {
        if (window is null)
        {
            builder.AppendLine($"{label}: (none)");
            return;
        }

        var resetInfo = window.ResetsAt is { } resetsAt
            ? $"resetsAtUnix={resetsAt.ToUnixTimeSeconds()}, resetsAtLocal={resetsAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}, " +
              $"minutesUntilReset={(resetsAt - now).TotalMinutes:0.0}"
            : "resetsAt=(none)";
        builder.AppendLine(
            $"{label}: title={WindowTitle(window.WindowMinutes)}, usedPercent={window.UsedPercent.ToString("0.##", CultureInfo.InvariantCulture)}, " +
            $"windowMinutes={window.WindowMinutes}, {resetInfo}");
    }

    private sealed record CodexRateLimitSnapshot(
        DateTimeOffset Timestamp,
        CodexWindow? Primary,
        CodexWindow? Secondary,
        string? PlanType)
    {
        public string? SourceFile { get; init; }
    }

    private sealed record CodexWindow(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt);
}
