using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

internal enum OverlayChromeMode
{
    Full,
    Compact,
    Mini
}

internal enum ProviderCardDetailLevel
{
    Full,
    Compact,
    MiniTall,
    Mini
}

internal readonly record struct CardContentProfile(
    int UsageWindowCount,
    bool HasStatusMessage = false,
    bool IsUnavailable = false)
{
    public int NormalizedWindowCount => Math.Max(0, UsageWindowCount);
}

internal sealed record ResponsiveLayoutInput(
    double PhysicalWidth,
    double PhysicalHeight,
    double UiScale,
    IReadOnlyList<CardContentProfile> Cards,
    double CompactButtonsRightInset,
    ResponsiveLayoutPlan? PreviousPlan = null);

internal sealed record ResponsiveLayoutPlan(
    OverlayChromeMode ChromeMode,
    ProviderCardDetailLevel DetailLevel,
    int Columns,
    int Rows,
    IReadOnlyList<int> RowItemCounts,
    double AvailableWidth,
    double AvailableHeight,
    double MinimumSlotWidth,
    double MaximumSlotWidth,
    double SlotHeight,
    double ContentScale,
    double ContentSpanTarget,
    double Score)
{
    public static ResponsiveLayoutPlan Empty(
        OverlayChromeMode chromeMode,
        double availableWidth,
        double availableHeight) =>
        new(
            chromeMode,
            ProviderCardDetailLevel.Mini,
            0,
            0,
            [],
            availableWidth,
            availableHeight,
            availableWidth,
            availableWidth,
            availableHeight,
            1,
            1,
            0);
}

/// <summary>
/// Pure, deterministic responsive-layout calculator. WPF measures are applied after this plan is
/// selected; keeping the decision logic free of UI objects makes the full supported size range cheap
/// to regression test.
/// </summary>
internal static class ResponsiveLayoutEngine
{
    internal const double CompactWidthBreakpoint = 760;
    internal const double MiniWidthBreakpoint = 460;
    internal const double FullHeightBreakpoint = 360;
    internal const double MiniHeightBreakpoint = 110;
    internal const double MiniHorizontalChromeInset = 54;
    internal const double CompactHorizontalChromeInset = 28;
    internal const double FullHorizontalChromeInset = 84;
    internal const double MiniVerticalChromeInset = 22;
    internal const double CompactVerticalChromeInset = 36;
    internal const double FullVerticalChromeInset = 210;
    internal const double MinimumMiniCardWidth = 136;
    internal const double MinimumMiniRowHeight = 38;
    internal const double MinimumContentScale = 0.72;
    internal const double MaximumContentScale = 1.5;

    private const double FullMinimumCellWidth = 265;
    private const double FullMinimumCellHeight = 250;
    private const double CompactMinimumCellWidth = 200;
    private const double CompactMinimumCellHeight = 90;
    private const double MiniTallMinimumCellWidth = 112;
    private const double MiniTallMinimumCellHeight = 82;
    private const double TargetCardAspect = 1.6;
    private const double LayoutChangeHysteresis = 5.5;
    private const double MiniFitPenaltyWeight = 120;
    private const double MiniHeightDeficitWeight = 1.5;
    private const double WideStripSingleRowBonus = 60;

    public static ResponsiveLayoutPlan Calculate(ResponsiveLayoutInput input)
    {
        var scale = NormalizeScale(input.UiScale);
        var logicalWidth = Math.Max(1, input.PhysicalWidth / scale);
        var logicalHeight = Math.Max(1, input.PhysicalHeight / scale);
        var chromeMode = GetChromeMode(logicalWidth, logicalHeight);
        var horizontalInset = chromeMode switch
        {
            OverlayChromeMode.Mini => MiniHorizontalChromeInset,
            OverlayChromeMode.Compact => CompactHorizontalChromeInset + Math.Max(0, input.CompactButtonsRightInset),
            _ => FullHorizontalChromeInset
        };
        var verticalInset = chromeMode switch
        {
            OverlayChromeMode.Mini => MiniVerticalChromeInset,
            OverlayChromeMode.Compact => CompactVerticalChromeInset,
            _ => FullVerticalChromeInset
        };
        var availableWidth = Math.Max(1, logicalWidth - horizontalInset);
        var availableHeight = Math.Max(1, logicalHeight - verticalInset);
        var cards = input.Cards ?? [];

        if (cards.Count == 0)
        {
            return ResponsiveLayoutPlan.Empty(chromeMode, availableWidth, availableHeight);
        }

        var candidates = new List<ResponsiveLayoutPlan>(cards.Count);
        for (var rows = 1; rows <= cards.Count; rows++)
        {
            var rowItemCounts = BuildBalancedRows(cards.Count, rows);
            var largestRow = rowItemCounts.Max();
            var smallestRow = rowItemCounts.Min();
            var columns = largestRow;
            var minimumSlotWidth = availableWidth / largestRow;
            var maximumSlotWidth = availableWidth / smallestRow;
            var slotHeight = availableHeight / rows;
            if (minimumSlotWidth <= 0 || slotHeight <= 0)
            {
                continue;
            }

            var detailLevel = GetDetailLevel(minimumSlotWidth, slotHeight);
            if (!FitsContent(detailLevel, slotHeight, cards))
            {
                detailLevel = GetNextSmallerDetail(detailLevel, minimumSlotWidth, slotHeight, cards);
            }

            var contentScale = CalculateContentScale(detailLevel, minimumSlotWidth, slotHeight, cards);
            var contentSpan = CalculateContentSpan(detailLevel, slotHeight, contentScale, cards);
            var informationScore = GetInformationRank(detailLevel) * 100;
            var utilizationScore = contentSpan * 100;
            var averageSlotWidth = rowItemCounts.Average(count => availableWidth / count);
            var aspect = averageSlotWidth / slotHeight;
            var aspectComfort = Math.Exp(-Math.Abs(Math.Log(Math.Max(0.01, aspect) / TargetCardAspect)));
            var aspectScore = aspectComfort * 15;
            var rowBalance = rows == 1 ? 1 : smallestRow / (double)largestRow;
            var balanceScore = rowBalance * 15;
            var stabilityBonus = detailLevel != ProviderCardDetailLevel.Mini &&
                                 MatchesLayout(input.PreviousPlan, chromeMode, detailLevel, columns, rows)
                ? LayoutChangeHysteresis
                : 0;
            var miniFitPenalty = detailLevel == ProviderCardDetailLevel.Mini
                ? CalculateMiniFitPenalty(minimumSlotWidth, slotHeight)
                : 0;
            var wideStripBonus = detailLevel == ProviderCardDetailLevel.Mini &&
                                 rows == 1 &&
                                 minimumSlotWidth >= MinimumMiniCardWidth &&
                                 slotHeight >= MinimumMiniRowHeight
                ? WideStripSingleRowBonus
                : 0;
            var score = informationScore + utilizationScore + aspectScore + balanceScore + stabilityBonus - miniFitPenalty + wideStripBonus;

            candidates.Add(new ResponsiveLayoutPlan(
                chromeMode,
                detailLevel,
                columns,
                rows,
                rowItemCounts,
                availableWidth,
                availableHeight,
                minimumSlotWidth,
                maximumSlotWidth,
                slotHeight,
                contentScale,
                Math.Clamp(Math.Max(contentSpan, 0.78), 0.78, 0.94),
                score));
        }

        if (candidates.Count == 0)
        {
            return ResponsiveLayoutPlan.Empty(chromeMode, availableWidth, availableHeight);
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => GetInformationRank(candidate.DetailLevel))
            .ThenBy(candidate => candidate.Rows)
            .ThenBy(candidate => candidate.Columns)
            .First();
    }

    internal static IReadOnlyList<int> BuildBalancedRows(int itemCount, int rows)
    {
        if (itemCount <= 0 || rows <= 0)
        {
            return [];
        }

        rows = Math.Clamp(rows, 1, itemCount);
        var baseCount = itemCount / rows;
        var extraItems = itemCount % rows;
        var result = new int[rows];
        for (var row = 0; row < rows; row++)
        {
            result[row] = baseCount + (row < extraItems ? 1 : 0);
        }

        return result;
    }

    internal static OverlayChromeMode GetChromeMode(double logicalWidth, double logicalHeight)
    {
        if (logicalWidth < MiniWidthBreakpoint || logicalHeight < MiniHeightBreakpoint)
        {
            return OverlayChromeMode.Mini;
        }

        if (logicalWidth < CompactWidthBreakpoint || logicalHeight < FullHeightBreakpoint)
        {
            return OverlayChromeMode.Compact;
        }

        return OverlayChromeMode.Full;
    }

    internal static ProviderCardDetailLevel GetDetailLevel(double cellWidth, double cellHeight)
    {
        if (cellWidth >= FullMinimumCellWidth && cellHeight >= FullMinimumCellHeight)
        {
            return ProviderCardDetailLevel.Full;
        }

        if (cellWidth >= CompactMinimumCellWidth && cellHeight >= CompactMinimumCellHeight)
        {
            return ProviderCardDetailLevel.Compact;
        }

        if (cellWidth >= MiniTallMinimumCellWidth && cellHeight >= MiniTallMinimumCellHeight)
        {
            return ProviderCardDetailLevel.MiniTall;
        }

        return ProviderCardDetailLevel.Mini;
    }

    private static ProviderCardDetailLevel GetNextSmallerDetail(
        ProviderCardDetailLevel detailLevel,
        double cellWidth,
        double cellHeight,
        IReadOnlyList<CardContentProfile> cards)
    {
        var candidate = detailLevel;
        while (candidate != ProviderCardDetailLevel.Mini)
        {
            candidate = candidate switch
            {
                ProviderCardDetailLevel.Full => ProviderCardDetailLevel.Compact,
                ProviderCardDetailLevel.Compact => cellWidth >= MiniTallMinimumCellWidth && cellHeight >= MiniTallMinimumCellHeight
                    ? ProviderCardDetailLevel.MiniTall
                    : ProviderCardDetailLevel.Mini,
                _ => ProviderCardDetailLevel.Mini
            };

            if (FitsContent(candidate, cellHeight, cards))
            {
                return candidate;
            }
        }

        return ProviderCardDetailLevel.Mini;
    }

    private static bool FitsContent(
        ProviderCardDetailLevel detailLevel,
        double slotHeight,
        IReadOnlyList<CardContentProfile> cards)
    {
        if (detailLevel is ProviderCardDetailLevel.Compact or ProviderCardDetailLevel.Mini)
        {
            return true;
        }

        return cards.All(card =>
        {
            var outerInset = GetVerticalOuterInset(detailLevel);
            var naturalHeight = EstimateNaturalContentHeight(detailLevel, card);
            var requiredHeight = outerInset + naturalHeight * MinimumContentScale;
            return requiredHeight <= slotHeight + 0.5;
        });
    }

    private static double CalculateContentScale(
        ProviderCardDetailLevel detailLevel,
        double slotWidth,
        double slotHeight,
        IReadOnlyList<CardContentProfile> cards)
    {
        var baseWidth = detailLevel switch
        {
            ProviderCardDetailLevel.Full => 300,
            ProviderCardDetailLevel.Compact => 260,
            ProviderCardDetailLevel.MiniTall => 140,
            _ => 180
        };
        var averageNaturalHeight = Math.Max(1, cards.Average(card => EstimateNaturalContentHeight(detailLevel, card)));
        var outerInset = GetVerticalOuterInset(detailLevel);
        var heightLimit = Math.Max(1, slotHeight - outerInset) / averageNaturalHeight;
        var widthLimit = Math.Max(1, slotWidth - 20) / baseWidth;
        var preferred = Math.Min(widthLimit, heightLimit);
        var minimum = detailLevel == ProviderCardDetailLevel.Mini ? 1 : MinimumContentScale;
        return Math.Clamp(preferred, minimum, MaximumContentScale);
    }

    private static double CalculateContentSpan(
        ProviderCardDetailLevel detailLevel,
        double slotHeight,
        double contentScale,
        IReadOnlyList<CardContentProfile> cards)
    {
        var outerInset = GetVerticalOuterInset(detailLevel);
        var contentHeight = cards.Average(card => EstimateNaturalContentHeight(detailLevel, card)) * contentScale;
        return Math.Clamp((outerInset + contentHeight) / Math.Max(1, slotHeight), 0, 1);
    }

    private static double EstimateNaturalContentHeight(ProviderCardDetailLevel detailLevel, CardContentProfile card)
    {
        var windowCount = card.NormalizedWindowCount;
        return detailLevel switch
        {
            ProviderCardDetailLevel.Full =>
                42 + (card.HasStatusMessage && card.IsUnavailable ? 38 : 0) + (windowCount > 0 ? 16 + windowCount * 79 : 0),
            ProviderCardDetailLevel.Compact => 22 + (windowCount > 0 ? 8 + windowCount * 20 : 0),
            ProviderCardDetailLevel.MiniTall => 17 + Math.Max(1, windowCount) * 26,
            _ => 25
        };
    }

    private static double GetVerticalOuterInset(ProviderCardDetailLevel detailLevel)
    {
        return detailLevel switch
        {
            ProviderCardDetailLevel.Full => 46,
            ProviderCardDetailLevel.Compact => 24,
            ProviderCardDetailLevel.MiniTall => 22,
            _ => 12
        };
    }

    private static double CalculateMiniFitPenalty(double slotWidth, double slotHeight)
    {
        var widthDeficit = Math.Max(0, (MinimumMiniCardWidth - slotWidth) / MinimumMiniCardWidth);
        var heightDeficit = Math.Max(0, (MinimumMiniRowHeight - slotHeight) / MinimumMiniRowHeight);
        return MiniFitPenaltyWeight * (widthDeficit + MiniHeightDeficitWeight * heightDeficit);
    }

    private static int GetInformationRank(ProviderCardDetailLevel detailLevel)
    {
        return detailLevel switch
        {
            ProviderCardDetailLevel.Full => 3,
            ProviderCardDetailLevel.Compact => 2,
            ProviderCardDetailLevel.MiniTall => 1,
            _ => 0
        };
    }

    private static bool MatchesLayout(
        ResponsiveLayoutPlan? previous,
        OverlayChromeMode chromeMode,
        ProviderCardDetailLevel detailLevel,
        int columns,
        int rows)
    {
        return previous is not null &&
               previous.ChromeMode == chromeMode &&
               previous.DetailLevel == detailLevel &&
               previous.Columns == columns &&
               previous.Rows == rows;
    }

    private static double NormalizeScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return AppSettings.DefaultUiScalePercent / 100d;
        }

        return Math.Clamp(
            scale,
            AppSettings.MinimumUiScalePercent / 100d,
            AppSettings.MaximumUiScalePercent / 100d);
    }
}
