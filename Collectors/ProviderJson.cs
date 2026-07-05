using System.Globalization;
using System.Text.Json;

namespace AIUsageMonitor.Collectors;

internal static class ProviderJson
{
    private const DateTimeStyles ProviderDateTimeStyles =
        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal;

    public static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;

        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDouble(out value),
            JsonValueKind.String => TryParseDouble(property.GetString(), out value),
            _ => false
        };
    }

    public static bool TryParseDouble(string? text, out double value)
    {
        return double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    public static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;

        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                property.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }

    public static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;

        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt64(out value),
            JsonValueKind.String => TryParseInt64(property.GetString(), out value),
            _ => false
        };
    }

    public static bool TryParseInt64(string? text, out long value)
    {
        return long.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    public static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    public static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            TryParseDateTimeOffset(property.GetString(), out var parsed)
                ? parsed
                : null;
    }

    public static bool TryParseDateTimeOffset(string? text, out DateTimeOffset value)
    {
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            ProviderDateTimeStyles,
            out value);
    }

    public static DateTimeOffset? TryGetUnixSeconds(JsonElement element, string propertyName)
    {
        if (!TryGetInt64(element, propertyName, out var unixSeconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static DateTimeOffset? TryGetUnixMilliseconds(JsonElement element, string propertyName)
    {
        if (!TryGetInt64(element, propertyName, out var unixMilliseconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
