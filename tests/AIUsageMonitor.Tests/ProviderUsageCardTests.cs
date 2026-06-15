using AIUsageMonitor.Models;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ProviderUsageCardTests
{
    [TestMethod]
    public void InactiveModelDoesNotDragCardToExhausted()
    {
        // Reproduces the free-Google-account case: usable "Flash" is full, but an unusable "Pro"
        // bucket comes back empty/inactive. The card headline + badge must come from Flash only.
        var usage = new ProviderUsage
        {
            Name = "Gemini",
            Source = "Test",
            StatusMessage = "Test usage.",
            LastCheckedAt = DateTimeOffset.Now,
            Windows =
            [
                new UsageWindow
                {
                    Title = "Gemini Flash",
                    Limit = 100,
                    Used = 0,
                    Remaining = 100,
                    ResetAt = DateTimeOffset.Now.AddHours(24)
                },
                new UsageWindow
                {
                    Title = "Gemini Pro",
                    Limit = 100,
                    Used = 100,
                    Remaining = 0,
                    ResetAt = null,
                    Detail = "Not on this plan",
                    IsInactive = true
                }
            ]
        };

        var card = new ProviderUsageCard(usage);

        Assert.AreEqual(100d, card.PrimaryRemainingPercent);
        Assert.AreEqual("Healthy", card.OverallStatusLabel);
        StringAssert.Contains(card.SummaryText, "100%");

        // The inactive row is still shown, just muted and excluded from the card status.
        Assert.AreEqual(2, card.Windows.Count);
        var pro = card.Windows.Single(window => window.Title == "Gemini Pro");
        Assert.IsTrue(pro.IsInactive);
        Assert.AreEqual("N/A", pro.RemainingText);
        Assert.AreEqual("Not on this plan", pro.LimitText);
        Assert.AreEqual(string.Empty, pro.ResetRelativeText);
    }

    [TestMethod]
    public void CardWithOnlyInactiveModelsReportsNoData()
    {
        var usage = new ProviderUsage
        {
            Name = "Gemini",
            Source = "Test",
            StatusMessage = "Test usage.",
            LastCheckedAt = DateTimeOffset.Now,
            Windows =
            [
                new UsageWindow
                {
                    Title = "Gemini Pro",
                    Limit = 100,
                    Used = 100,
                    Remaining = 0,
                    IsInactive = true
                }
            ]
        };

        var card = new ProviderUsageCard(usage);

        Assert.AreEqual("No data", card.OverallStatusLabel);
        Assert.AreEqual(0d, card.PrimaryRemainingPercent);
    }
}
