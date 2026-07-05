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

    [TestMethod]
    public void BalanceWindowUsesAmountSummaryWithoutProgress()
    {
        var usage = new ProviderUsage
        {
            Name = KnownProviders.AnthropicApiCredits,
            Source = "Test",
            StatusMessage = "Test usage.",
            LastCheckedAt = DateTimeOffset.Now,
            Windows =
            [
                new UsageWindow
                {
                    Title = "Prepaid credits",
                    Limit = 100,
                    Used = 0,
                    Remaining = 100,
                    RemainingText = "$170.00 left",
                    Detail = "Verified now",
                    HideReset = true,
                    IsBalance = true
                }
            ]
        };

        var card = new ProviderUsageCard(usage);

        Assert.AreEqual("Anthropic API Credits - $170.00 left", card.SummaryText);
        Assert.AreEqual("$170.00 left - Anthropic API Credits", card.CompactSummaryText);
        Assert.AreEqual("$170.00 left", card.MiniSummaryText);
        Assert.AreEqual("$170.00 left", card.BalanceSummaryText);
        Assert.IsTrue(card.IsBalanceOnly);
        Assert.IsFalse(card.ShowsSummaryProgress);
        Assert.AreEqual(1, card.Windows.Count);
        Assert.IsTrue(card.Windows[0].IsBalance);
        Assert.IsFalse(card.Windows[0].ShowsProgress);
    }

    [TestMethod]
    public void SupplementalDisplayGroupsRenderAsSeparateProviderCards()
    {
        var viewModel = new UsageOverlayViewModel();
        var usage = new ProviderUsage
        {
            Name = "Anthropic",
            PlanName = "Max 20x",
            Source = "Test",
            StatusMessage = "Test usage.",
            LastCheckedAt = DateTimeOffset.Now,
            Windows =
            [
                new UsageWindow
                {
                    Title = "5h",
                    Limit = 100,
                    Used = 18,
                    Remaining = 82,
                    ResetAt = DateTimeOffset.Now.AddHours(3)
                },
                new UsageWindow
                {
                    Title = "Fable",
                    DisplayGroupName = "Fable",
                    Limit = 100,
                    Used = 100,
                    Remaining = 0,
                    ResetAt = DateTimeOffset.Now.AddHours(14)
                },
                new UsageWindow
                {
                    Title = "Extra usage",
                    DisplayGroupName = "Fable",
                    Limit = 50,
                    Used = 10,
                    Remaining = 40,
                    RemainingText = "$10 of $50",
                    Detail = "Monthly spend limit",
                    HideReset = true
                }
            ]
        };

        viewModel.ApplyProvider(usage);

        Assert.AreEqual(2, viewModel.Providers.Count);
        Assert.AreEqual("Anthropic (Max 20x)", viewModel.Providers[0].Name);
        Assert.AreEqual("Anthropic Fable", viewModel.Providers[1].Name);
        Assert.IsTrue(viewModel.Providers.All(provider => provider.SourceProviderName == "Anthropic"));
        Assert.AreEqual("Exhausted", viewModel.Providers[1].OverallStatusLabel);
        Assert.AreEqual(2, viewModel.Providers[1].Windows.Count);
        Assert.AreEqual("$10 of $50", viewModel.Providers[1].Windows.Single(window => window.Title == "Extra usage").RemainingText);

        viewModel.SetChecking(["Anthropic"]);

        Assert.AreEqual(2, viewModel.Providers.Count);
        Assert.IsTrue(viewModel.Providers.All(provider => provider.SummaryText.Contains("checking", StringComparison.OrdinalIgnoreCase)));
    }
}
