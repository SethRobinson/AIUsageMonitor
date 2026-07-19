using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ResponsiveLayoutEngineTests
{
    private const double CompactButtonsInset = 185;

    [TestMethod]
    public void ReportedFiveCardCaseUsesBalancedThreePlusTwoLayout()
    {
        var plan = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
            1593,
            409,
            1,
            CreateProfiles(2, 1, 2, 1, 1),
            CompactButtonsInset));

        Assert.AreEqual(OverlayChromeMode.Full, plan.ChromeMode);
        Assert.AreEqual(ProviderCardDetailLevel.Compact, plan.DetailLevel);
        Assert.AreEqual(3, plan.Columns);
        Assert.AreEqual(2, plan.Rows);
        CollectionAssert.AreEqual(new[] { 3, 2 }, plan.RowItemCounts.ToArray());
        Assert.IsTrue(plan.ContentSpanTarget >= 0.78);
    }

    [TestMethod]
    public void WideMiniStripNeverKeepsAStaleOneColumnLayout()
    {
        var profiles = CreateProfiles(2, 1, 2, 1, 1);
        var staleColumn = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
            320,
            760,
            1,
            profiles,
            CompactButtonsInset));
        var plan = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
            1679,
            97,
            1,
            profiles,
            CompactButtonsInset,
            staleColumn));

        Assert.AreEqual(OverlayChromeMode.Mini, plan.ChromeMode);
        Assert.AreEqual(ProviderCardDetailLevel.Mini, plan.DetailLevel);
        Assert.AreEqual(5, plan.Columns);
        Assert.AreEqual(1, plan.Rows);
        CollectionAssert.AreEqual(new[] { 5 }, plan.RowItemCounts.ToArray());
        Assert.IsTrue(plan.MinimumSlotWidth >= ResponsiveLayoutEngine.MinimumMiniCardWidth);
        Assert.IsTrue(plan.SlotHeight >= ResponsiveLayoutEngine.MinimumMiniRowHeight);
    }

    [TestMethod]
    public void ReportedMediumHeightStripUsesOneCompactRow()
    {
        var plan = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
            1469,
            210,
            1,
            CreateProfiles(2, 1, 2, 1, 1),
            CompactButtonsInset));

        Assert.AreEqual(OverlayChromeMode.Compact, plan.ChromeMode);
        Assert.AreEqual(ProviderCardDetailLevel.Compact, plan.DetailLevel);
        Assert.AreEqual(5, plan.Columns);
        Assert.AreEqual(1, plan.Rows);
    }

    [TestMethod]
    public void FullSupportedMatrixAlwaysProducesFiniteBalancedLayouts()
    {
        for (var scalePercent = 80; scalePercent <= 150; scalePercent += 5)
        {
            var scale = scalePercent / 100d;
            for (var providerCount = 0; providerCount <= 12; providerCount++)
            {
                var profiles = Enumerable.Range(0, providerCount)
                    .Select(index => new CardContentProfile(
                        UsageWindowCount: index % 4,
                        HasStatusMessage: index % 5 == 0,
                        IsUnavailable: index % 5 == 0))
                    .ToArray();

                for (var width = 320; width <= 2560; width += 80)
                {
                    for (var height = 56; height <= 1440; height += 64)
                    {
                        var plan = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
                            width,
                            height,
                            scale,
                            profiles,
                            CompactButtonsInset));

                        AssertPlanIsValid(plan, providerCount, width, height, scalePercent);
                    }
                }
            }
        }
    }

    [TestMethod]
    public void BalancedRowsConsumeTheWholeRowWithoutReservedEmptyCells()
    {
        for (var itemCount = 1; itemCount <= 12; itemCount++)
        {
            for (var rows = 1; rows <= itemCount; rows++)
            {
                var rowItemCounts = ResponsiveLayoutEngine.BuildBalancedRows(itemCount, rows);
                Assert.AreEqual(itemCount, rowItemCounts.Sum());
                Assert.IsTrue(rowItemCounts.Max() - rowItemCounts.Min() <= 1);

                const double availableWidth = 1234.5;
                foreach (var count in rowItemCounts)
                {
                    var widthPerCard = availableWidth / count;
                    Assert.AreEqual(availableWidth, widthPerCard * count, 0.001);
                }
            }
        }
    }

    [TestMethod]
    public void PreviousPlanAddsThresholdHysteresisWithoutChangingExactLayout()
    {
        var profiles = CreateProfiles(2, 2, 1, 1, 1);
        var initial = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
            759,
            359,
            1,
            profiles,
            CompactButtonsInset));
        var repeated = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
            759,
            359,
            1,
            profiles,
            CompactButtonsInset,
            initial));

        Assert.AreEqual(initial.ChromeMode, repeated.ChromeMode);
        Assert.AreEqual(initial.DetailLevel, repeated.DetailLevel);
        Assert.AreEqual(initial.Columns, repeated.Columns);
        Assert.AreEqual(initial.Rows, repeated.Rows);
        Assert.IsTrue(repeated.Score > initial.Score);
    }

    [TestMethod]
    public void IncreasingEitherDimensionDoesNotReduceCombinedInformationLevel()
    {
        var profiles = CreateProfiles(2, 1, 2, 1, 1);
        foreach (var height in new[] { 56, 109, 110, 111, 136, 240, 359, 360, 361, 409, 740 })
        {
            var previousRank = int.MinValue;
            for (var width = 320; width <= 2560; width++)
            {
                var plan = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
                    width,
                    height,
                    1,
                    profiles,
                    CompactButtonsInset));
                var rank = GetCombinedInformationRank(plan);
                Assert.IsTrue(rank >= previousRank, $"Information regressed at {width}x{height}.");
                previousRank = rank;
            }
        }

        foreach (var width in new[] { 320, 459, 460, 461, 600, 759, 760, 761, 900, 1440, 1593, 2560 })
        {
            var previousRank = int.MinValue;
            for (var height = 56; height <= 1440; height++)
            {
                var plan = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
                    width,
                    height,
                    1,
                    profiles,
                    CompactButtonsInset));
                var rank = GetCombinedInformationRank(plan);
                Assert.IsTrue(rank >= previousRank, $"Information regressed at {width}x{height}.");
                previousRank = rank;
            }
        }
    }

    [TestMethod]
    public void HysteresisPreventsOnePixelLayoutOscillation()
    {
        var profiles = CreateProfiles(2, 1, 2, 1, 1);
        foreach (var height in new[] { 110, 136, 240, 359, 360, 409, 740 })
        {
            ResponsiveLayoutPlan? previous = null;
            string? beforePreviousSignature = null;
            string? previousSignature = null;
            for (var width = 320; width <= 2560; width++)
            {
                var plan = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
                    width,
                    height,
                    1,
                    profiles,
                    CompactButtonsInset,
                    previous));
                var signature = $"{plan.ChromeMode}/{plan.DetailLevel}/{plan.Columns}/{plan.Rows}";
                Assert.IsFalse(
                    signature == beforePreviousSignature && signature != previousSignature,
                    $"One-pixel A-B-A oscillation at {width}x{height}: {beforePreviousSignature}, {previousSignature}, {signature}.");
                beforePreviousSignature = previousSignature;
                previousSignature = signature;
                previous = plan;
            }
        }
    }

    private static CardContentProfile[] CreateProfiles(params int[] windowCounts)
    {
        return windowCounts.Select(count => new CardContentProfile(count)).ToArray();
    }

    private static int GetCombinedInformationRank(ResponsiveLayoutPlan plan)
    {
        var chromeRank = plan.ChromeMode switch
        {
            OverlayChromeMode.Full => 2,
            OverlayChromeMode.Compact => 1,
            _ => 0
        };
        var cardRank = plan.DetailLevel switch
        {
            ProviderCardDetailLevel.Full => 3,
            ProviderCardDetailLevel.Compact => 2,
            ProviderCardDetailLevel.MiniTall => 1,
            _ => 0
        };
        return chromeRank + cardRank;
    }

    private static void AssertPlanIsValid(
        ResponsiveLayoutPlan plan,
        int providerCount,
        int width,
        int height,
        int scalePercent)
    {
        var description = $"{width}x{height} at {scalePercent}% with {providerCount} providers";
        Assert.IsTrue(double.IsFinite(plan.AvailableWidth) && plan.AvailableWidth > 0, description);
        Assert.IsTrue(double.IsFinite(plan.AvailableHeight) && plan.AvailableHeight > 0, description);
        Assert.IsTrue(double.IsFinite(plan.MinimumSlotWidth) && plan.MinimumSlotWidth > 0, description);
        Assert.IsTrue(double.IsFinite(plan.MaximumSlotWidth) && plan.MaximumSlotWidth > 0, description);
        Assert.IsTrue(double.IsFinite(plan.SlotHeight) && plan.SlotHeight > 0, description);
        Assert.IsTrue(double.IsFinite(plan.Score), description);

        if (providerCount == 0)
        {
            Assert.AreEqual(0, plan.Columns, description);
            Assert.AreEqual(0, plan.Rows, description);
            Assert.AreEqual(0, plan.RowItemCounts.Count, description);
            return;
        }

        Assert.IsTrue(plan.Columns >= 1 && plan.Columns <= providerCount, description);
        Assert.IsTrue(plan.Rows >= 1 && plan.Rows <= providerCount, description);
        Assert.AreEqual(providerCount, plan.RowItemCounts.Sum(), description);
        Assert.IsTrue(plan.RowItemCounts.Max() - plan.RowItemCounts.Min() <= 1, description);
        Assert.IsTrue(plan.ContentScale >= ResponsiveLayoutEngine.MinimumContentScale - 0.001, description);
        Assert.IsTrue(plan.ContentScale <= ResponsiveLayoutEngine.MaximumContentScale + 0.001, description);
        Assert.IsTrue(plan.ContentSpanTarget is >= 0.78 and <= 0.94, description);
    }
}
