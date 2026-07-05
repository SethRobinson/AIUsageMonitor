using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Collectors;

public sealed class GeminiUsageCollector : IUsageCollector
{
    // NOTE: These are NOT private secrets. They are the public OAuth client credentials
    // that ship inside Google's open-source Gemini CLI (google-gemini/gemini-cli, file
    // packages/core/src/code_assist/oauth2.ts). Per Google's OAuth2 installed-application
    // guidelines, the "client secret" for an installed app is not treated as a secret:
    // https://developers.google.com/identity/protocols/oauth2#installed
    // We use the same credentials here so this app can talk to the same Gemini Code Assist
    // OAuth endpoints that the official Gemini CLI uses. GitHub's secret scanner may flag
    // these on push; that's expected, and they can be safely allowed.
    private const string OAuthClientId = "681255809395-oo8ft2oprdrnp9e3aqf6av3hmdib135j.apps.googleusercontent.com";
    private const string OAuthClientSecret = "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";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string CodeAssistEndpoint = "https://cloudcode-pa.googleapis.com/v1internal";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string ProviderName => KnownProviders.Gemini;

    public async Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var geminiDirectory = Path.Combine(home, ".gemini");

        var codeAssistUsage = await TryCollectCodeAssistQuotaAsync(geminiDirectory, cancellationToken);
        if (codeAssistUsage is not null)
        {
            return codeAssistUsage;
        }

        var candidatePaths = new[]
        {
            Path.Combine(geminiDirectory, "ai-usage-monitor-usage.json"),
            Path.Combine(geminiDirectory, "usage-status.json")
        };

        foreach (var path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                continue;
            }

            var usage = TryParseStatusJson(File.ReadAllText(path), path);
            if (usage is not null)
            {
                return usage;
            }
        }

        var isGeminiCliInstalled = IsCommandAvailable("gemini");
        var latestSessionUsage = TryCollectLatestSessionUsage(geminiDirectory, isGeminiCliInstalled);
        if (latestSessionUsage is not null)
        {
            return latestSessionUsage;
        }

        var message = isGeminiCliInstalled
            ? "Gemini CLI is installed, but no Code Assist OAuth credentials, quota status export, or local session usage was found."
            : "Gemini CLI is not installed and no Gemini status export was found.";

        return ProviderUsageFactory.Unavailable(ProviderName, message, geminiDirectory);
    }

    private async Task<ProviderUsage?> TryCollectCodeAssistQuotaAsync(string geminiDirectory, CancellationToken cancellationToken)
    {
        var credentialsPath = Path.Combine(geminiDirectory, "oauth_creds.json");
        if (!File.Exists(credentialsPath))
        {
            return null;
        }

        var credentials = await LoadOAuthCredentialsAsync(credentialsPath, cancellationToken);
        var accessToken = await GetUsableAccessTokenAsync(credentialsPath, credentials, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return ProviderUsageFactory.Unavailable(
                ProviderName,
                "Gemini CLI OAuth credentials exist, but no usable access token could be loaded. Re-run Gemini CLI login.",
                credentialsPath);
        }

        using var loadResponse = await PostCodeAssistAsync(
            "loadCodeAssist",
            BuildLoadCodeAssistRequest(),
            accessToken,
            cancellationToken);

        var projectId = TryGetString(loadResponse.RootElement, "cloudaicompanionProject");
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return ProviderUsageFactory.Unavailable(
                ProviderName,
                "Gemini Code Assist authenticated, but the quota project was not returned.",
                "Gemini Code Assist quota API");
        }

        using var quotaResponse = await PostCodeAssistAsync(
            "retrieveUserQuota",
            new Dictionary<string, object?> { ["project"] = projectId },
            accessToken,
            cancellationToken);

        return ParseCodeAssistQuota(
            quotaResponse.RootElement,
            projectId,
            TryGetTierName(loadResponse.RootElement));
    }

    private static async Task<JsonObject> LoadOAuthCredentialsAsync(string credentialsPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(credentialsPath);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        return node as JsonObject ?? [];
    }

    private static async Task<string?> GetUsableAccessTokenAsync(
        string credentialsPath,
        JsonObject credentials,
        CancellationToken cancellationToken)
    {
        var accessToken = credentials["access_token"]?.GetValue<string>();
        var expiryDate = TryGetLong(credentials, "expiry_date");

        if (!string.IsNullOrWhiteSpace(accessToken) &&
            expiryDate is not null &&
            DateTimeOffset.FromUnixTimeMilliseconds(expiryDate.Value) > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return accessToken;
        }

        var refreshToken = credentials["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return accessToken;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = refreshToken,
                ["client_id"] = OAuthClientId,
                ["client_secret"] = OAuthClientSecret,
                ["grant_type"] = "refresh_token"
            })
        };

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (!root.TryGetProperty("access_token", out var accessTokenElement))
        {
            return accessToken;
        }

        var refreshedAccessToken = accessTokenElement.GetString();
        if (string.IsNullOrWhiteSpace(refreshedAccessToken))
        {
            return accessToken;
        }

        credentials["access_token"] = refreshedAccessToken;
        credentials["token_type"] = TryGetString(root, "token_type") ?? "Bearer";

        if (root.TryGetProperty("expires_in", out var expiresInElement) &&
            expiresInElement.TryGetInt64(out var expiresInSeconds))
        {
            credentials["expiry_date"] = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeMilliseconds();
        }

        try
        {
            await File.WriteAllTextAsync(
                credentialsPath,
                credentials.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return refreshedAccessToken;
    }

    private static Dictionary<string, object?> BuildLoadCodeAssistRequest()
    {
        var projectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ??
            Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT_ID");
        var metadata = new Dictionary<string, string>
        {
            ["ideType"] = "IDE_UNSPECIFIED",
            ["platform"] = "PLATFORM_UNSPECIFIED",
            ["pluginType"] = "GEMINI"
        };

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            metadata["duetProject"] = projectId;
        }

        var request = new Dictionary<string, object?>
        {
            ["metadata"] = metadata
        };

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            request["cloudaicompanionProject"] = projectId;
        }

        return request;
    }

    private static async Task<JsonDocument> PostCodeAssistAsync(
        string method,
        object payload,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{CodeAssistEndpoint}:{method}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static ProviderUsage ParseCodeAssistQuota(JsonElement root, string projectId, string? tierName)
    {
        var planName = PlanNameFormatter.Format(tierName);

        if (!root.TryGetProperty("buckets", out var bucketsElement) ||
            bucketsElement.ValueKind != JsonValueKind.Array)
        {
            return ProviderUsageFactory.Unavailable(
                "Gemini",
                "Gemini Code Assist quota response did not include quota buckets.",
                "Gemini Code Assist quota API",
                planName);
        }

        var bucketsByFamily = bucketsElement
            .EnumerateArray()
            .Select(TryParseQuotaBucket)
            .Where(bucket => bucket is not null)
            .Select(bucket => bucket!)
            .GroupBy(bucket => GetModelFamily(bucket.ModelId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var windows = new List<UsageWindow>();
        foreach (var group in bucketsByFamily)
        {
            var buckets = group.ToList();

            // A family with no remaining quota AND no future reset has no live allocation — this is
            // what a free Google account returns for "Pro" (an empty bucket whose reset is already in
            // the past). Mark it inactive rather than letting it read as a real 0% / Exhausted window.
            // A paid account that has genuinely burned its Pro cap still has a *future* reset, so it
            // stays a normal exhausted window.
            var hasRemaining = buckets.Any(bucket => bucket.RemainingFraction > 0);
            var hasFutureReset = buckets.Any(bucket => bucket.ResetAt is { } reset && reset > now);
            if (!hasRemaining && !hasFutureReset)
            {
                windows.Add(ProviderUsageFactory.InactiveWindow($"Gemini {group.Key}"));
                continue;
            }

            var lowestBucket = buckets.OrderBy(bucket => bucket.RemainingFraction).First();
            var remainingPercent = Math.Clamp(lowestBucket.RemainingFraction * 100d, 0, 100);
            windows.Add(ProviderUsageFactory.PercentWindow(
                $"Gemini {group.Key}",
                100 - remainingPercent,
                lowestBucket.ResetAt,
                $"{remainingPercent:0.#}% left across {buckets.Count} model bucket(s)"));
        }

        if (windows.Count == 0)
        {
            return ProviderUsageFactory.Unavailable(
                "Gemini",
                "Gemini Code Assist quota response did not contain readable quota percentages.",
                "Gemini Code Assist quota API",
                planName);
        }

        return new ProviderUsage
        {
            Name = "Gemini",
            PlanName = planName,
            Source = $"Gemini Code Assist quota API ({projectId})",
            StatusMessage = string.IsNullOrWhiteSpace(planName)
                ? "Gemini quota from Code Assist."
                : $"{planName} quota from Code Assist.",
            Windows = windows
        };
    }

    private static ProviderUsage? TryParseStatusJson(string text, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var windows = new List<UsageWindow>();

            if (root.TryGetProperty("windows", out var windowsElement) && windowsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var windowElement in windowsElement.EnumerateArray())
                {
                    var title = windowElement.TryGetProperty("title", out var titleElement)
                        ? titleElement.GetString() ?? "Usage"
                        : "Usage";
                    var usedPercent = ProviderJson.TryGetDouble(windowElement, "usedPercent", out var parsedUsedPercent)
                        ? parsedUsedPercent
                        : 0;
                    var resetAt = ProviderJson.TryGetDateTimeOffset(windowElement, "resetAt");

                    windows.Add(ProviderUsageFactory.PercentWindow(title, usedPercent, resetAt));
                }
            }

            if (windows.Count == 0)
            {
                return null;
            }

            return new ProviderUsage
            {
                Name = "Gemini",
                PlanName = TryGetPlanName(root),
                Source = path,
                StatusMessage = "Gemini quota from local status export.",
                Windows = windows
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProviderUsage? TryCollectLatestSessionUsage(string geminiDirectory, bool isGeminiCliInstalled)
    {
        var tmpDirectory = Path.Combine(geminiDirectory, "tmp");
        if (!isGeminiCliInstalled || !Directory.Exists(tmpDirectory))
        {
            return null;
        }

        var latest = Directory.EnumerateFiles(tmpDirectory, "*.jsonl", SearchOption.AllDirectories)
            .Select(TryReadLatestSessionRecord)
            .Where(record => record is not null)
            .Select(record => record!)
            .OrderByDescending(record => record.Timestamp)
            .FirstOrDefault();

        if (latest is null)
        {
            return null;
        }

        return ProviderUsageFactory.Unavailable(
            "Gemini",
            $"Gemini CLI is installed. Quota was unavailable, but the latest local CLI session used {latest.TotalTokens:n0} tokens on {latest.Model}.",
            latest.SourcePath);
    }

    private static GeminiSessionRecord? TryReadLatestSessionRecord(string path)
    {
        GeminiSessionRecord? latest = null;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("tokens", out var tokens) ||
                    !TryGetLong(tokens, "total", out var totalTokens) ||
                    totalTokens <= 0)
                {
                    continue;
                }

                var timestamp = TryGetDateTimeOffset(root, "timestamp") ?? new DateTimeOffset(File.GetLastWriteTime(path));
                var model = TryGetString(root, "model") ?? "Gemini";
                var record = new GeminiSessionRecord(model, totalTokens, timestamp, path);

                if (latest is null || record.Timestamp > latest.Timestamp)
                {
                    latest = record;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }

        return latest;
    }

    private static GeminiQuotaBucket? TryParseQuotaBucket(JsonElement element)
    {
        if (!ProviderJson.TryGetDouble(element, "remainingFraction", out var remainingFraction))
        {
            return null;
        }

        var modelId = TryGetString(element, "modelId") ?? "Model";
        var resetAt = TryGetDateTimeOffset(element, "resetTime");
        return new GeminiQuotaBucket(modelId, remainingFraction, resetAt);
    }

    private static string GetModelFamily(string modelId)
    {
        if (modelId.Contains("pro", StringComparison.OrdinalIgnoreCase))
        {
            return "Pro";
        }

        if (modelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
        {
            return "Flash";
        }

        return "Models";
    }

    private static string? TryGetTierName(JsonElement root)
    {
        if (root.TryGetProperty("paidTier", out var paidTier) &&
            paidTier.TryGetProperty("name", out var paidTierName) &&
            paidTierName.ValueKind == JsonValueKind.String)
        {
            return paidTierName.GetString();
        }

        if (root.TryGetProperty("currentTier", out var currentTier) &&
            currentTier.TryGetProperty("name", out var currentTierName) &&
            currentTierName.ValueKind == JsonValueKind.String)
        {
            return currentTierName.GetString();
        }

        return null;
    }

    private static string TryGetPlanName(JsonElement root)
    {
        return PlanNameFormatter.Format(
            TryGetString(root, "planName") ??
            TryGetString(root, "plan") ??
            TryGetString(root, "tierName"));
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return ProviderJson.TryGetString(element, propertyName);
    }

    private static long? TryGetLong(JsonObject jsonObject, string propertyName)
    {
        return jsonObject.TryGetPropertyValue(propertyName, out var node) &&
            node is JsonValue jsonValue &&
            jsonValue.TryGetValue<long>(out var value)
                ? value
                : null;
    }

    private static bool TryGetLong(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return ProviderJson.TryGetInt64(element, propertyName, out value);
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        return ProviderJson.TryGetDateTimeOffset(element, propertyName);
    }

    private static bool IsCommandAvailable(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.PS1")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                if (File.Exists(Path.Combine(directory, command + extension)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record GeminiQuotaBucket(string ModelId, double RemainingFraction, DateTimeOffset? ResetAt);

    private sealed record GeminiSessionRecord(string Model, long TotalTokens, DateTimeOffset Timestamp, string SourcePath);
}
