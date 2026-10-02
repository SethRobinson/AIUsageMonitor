using System.Globalization;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Collectors;

internal static class ClaudeUsageCredits
{
    // This is the prepaid Claude subscription balance, not unused monthly spend headroom
    // and not the separately authenticated Console/API credit balance.
    public static void AddBalance(List<UsageWindow> windows, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("spend", out var spend) || spend.ValueKind != JsonValueKind.Object ||
            !spend.TryGetProperty("balance", out var balance) || balance.ValueKind != JsonValueKind.Object ||
            !balance.TryGetProperty("amount_minor", out var minorElement) || minorElement.ValueKind != JsonValueKind.Number ||
            !minorElement.TryGetDecimal(out var minor) || minor < 0 ||
            !balance.TryGetProperty("exponent", out var exponentElement) || exponentElement.ValueKind != JsonValueKind.Number ||
            !exponentElement.TryGetInt32(out var exponent) || exponent is < 0 or > 6 ||
            !balance.TryGetProperty("currency", out var currencyElement) || currencyElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var currency = currencyElement.GetString()?.Trim().ToUpperInvariant() ?? string.Empty;
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
        {
            return;
        }

        var amount = minor;
        for (var index = 0; index < exponent; index++) amount /= 10m;
        var disabled = spend.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False;
        windows.Add(CreateBalance(amount, currency, exponent, disabled
            ? "Prepaid Claude usage balance; extra usage is disabled"
            : "Prepaid Claude usage balance, used after plan limits"));
    }

    internal static UsageWindow CreateBalance(decimal amount, string currency, int exponent, string detail)
    {
        var formatted = amount.ToString("F" + exponent, CultureInfo.InvariantCulture);
        var money = currency == "USD" ? "$" + formatted : $"{formatted} {currency}";
        return new UsageWindow
        {
            Title = "Usage credits",
            Remaining = (double)amount,
            Limit = 0, // An absolute balance has no percentage cap.
            RemainingText = $"{money} credits",
            Detail = detail,
            HideReset = true,
            IsBalance = true
        };
    }
}
