using System.Globalization;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Collectors;

/// <summary>
/// Purchased ChatGPT/Codex usage credits reported next to the plan rate-limit windows.
/// Codex spends these once the plan windows are exhausted, so they are shown as a balance row
/// on the OpenAI card rather than as another percentage.
/// </summary>
internal sealed record CodexCredits(bool HasCredits, bool Unlimited, double? Balance)
{
    public const string WindowTitle = "Credits";

    /// <summary>True when the account actually holds credits worth showing.</summary>
    public bool IsActive => Unlimited || HasCredits || Balance is > 0;

    /// <summary>
    /// Parses the credits block from a rate-limit snapshot. Accepts the app-server camelCase form
    /// (<c>hasCredits</c>) and the session-log snake_case form (<c>has_credits</c>).
    /// </summary>
    public static CodexCredits? TryParse(JsonElement rateLimits)
    {
        if (rateLimits.ValueKind != JsonValueKind.Object ||
            !rateLimits.TryGetProperty("credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hasCredits = TryGetBool(credits, "hasCredits") ?? TryGetBool(credits, "has_credits") ?? false;
        var unlimited = TryGetBool(credits, "unlimited") ?? false;
        double? balance = ProviderJson.TryGetDouble(credits, "balance", out var parsed) && double.IsFinite(parsed)
            ? Math.Max(parsed, 0)
            : null;

        return new CodexCredits(hasCredits, unlimited, balance);
    }

    /// <summary>Returns a balance window for the card, or null when there is nothing to show.</summary>
    public UsageWindow? ToWindow()
    {
        if (!IsActive)
        {
            return null;
        }

        return new UsageWindow
        {
            Title = WindowTitle,
            Limit = 100,
            Used = 0,
            Remaining = 100,
            RemainingText = FormatBalance(),
            Detail = "Purchased credits, used after plan limits",
            HideReset = true,
            IsBalance = true
        };
    }

    public string FormatBalance()
    {
        if (Unlimited)
        {
            return "Unlimited credits";
        }

        if (Balance is { } balance)
        {
            var text = balance >= 100
                ? Math.Round(balance).ToString("#,0", CultureInfo.CurrentCulture)
                : balance.ToString("0.##", CultureInfo.CurrentCulture);
            return $"{text} credits";
        }

        return "Credits available";
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;
    }
}
