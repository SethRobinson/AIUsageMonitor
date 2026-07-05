using System.Globalization;
using System.Windows.Data;

namespace AIUsageMonitor.Views;

public sealed class ProviderCardScaleConverter : IMultiValueConverter
{
    private const string FullDisplayMode = "Full";
    private const double FullCardVerticalOuterInset = 46;
    private const double FullCardHeaderHeight = 42;
    private const double FullCardStatusMessageHeight = 38;
    private const double FullCardWindowsTopMargin = 16;
    private const double FullCardUsageWindowHeight = 79;
    private const double MinimumFullCardScale = 0.72;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var baseScale = GetDouble(values, 0, 1d);
        var cardSlotHeight = GetDouble(values, 1, double.PositiveInfinity);
        var detailLevel = values.Length > 2 ? values[2] as string : null;
        if (!string.Equals(detailLevel, FullDisplayMode, StringComparison.Ordinal) ||
            !double.IsFinite(cardSlotHeight) ||
            cardSlotHeight <= FullCardVerticalOuterInset)
        {
            return baseScale;
        }

        var windowCount = GetInt(values, 3);
        var hasStatusMessage = GetBool(values, 4) && !string.IsNullOrWhiteSpace(values.Length > 5 ? values[5] as string : null);
        var estimatedContentHeight = EstimateFullCardContentHeight(windowCount, hasStatusMessage);
        var availableContentHeight = Math.Max(1, cardSlotHeight - FullCardVerticalOuterInset);
        var heightScaleLimit = availableContentHeight / estimatedContentHeight;

        return Math.Clamp(Math.Min(baseScale, heightScaleLimit), MinimumFullCardScale, baseScale);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static double EstimateFullCardContentHeight(int windowCount, bool hasStatusMessage)
    {
        var height = FullCardHeaderHeight;
        if (hasStatusMessage)
        {
            height += FullCardStatusMessageHeight;
        }

        if (windowCount > 0)
        {
            height += FullCardWindowsTopMargin + windowCount * FullCardUsageWindowHeight;
        }

        return Math.Max(1, height);
    }

    private static double GetDouble(IReadOnlyList<object> values, int index, double fallback)
    {
        if (values.Count <= index)
        {
            return fallback;
        }

        return values[index] switch
        {
            double value when double.IsFinite(value) => value,
            int value => value,
            _ => fallback
        };
    }

    private static int GetInt(IReadOnlyList<object> values, int index)
    {
        if (values.Count <= index)
        {
            return 0;
        }

        return values[index] switch
        {
            int value => Math.Max(0, value),
            long value => (int)Math.Clamp(value, 0, int.MaxValue),
            double value when double.IsFinite(value) => Math.Max(0, (int)Math.Round(value)),
            _ => 0
        };
    }

    private static bool GetBool(IReadOnlyList<object> values, int index)
    {
        return values.Count > index && values[index] is bool value && value;
    }
}
