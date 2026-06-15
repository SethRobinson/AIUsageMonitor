using AIUsageMonitor.Models;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class AppSettingsTests
{
    [TestMethod]
    public void NormalizeKeepsDefaultUiScalePercent()
    {
        var settings = new AppSettings();

        settings.Normalize();

        Assert.AreEqual(AppSettings.DefaultUiScalePercent, settings.UiScalePercent);
    }

    [TestMethod]
    public void NormalizeDefaultsNonPositiveUiScalePercent()
    {
        var settings = new AppSettings
        {
            UiScalePercent = 0
        };

        settings.Normalize();

        Assert.AreEqual(AppSettings.DefaultUiScalePercent, settings.UiScalePercent);
    }

    [TestMethod]
    public void NormalizeClampsUiScalePercentBelowMinimum()
    {
        var settings = new AppSettings
        {
            UiScalePercent = AppSettings.MinimumUiScalePercent - 1
        };

        settings.Normalize();

        Assert.AreEqual(AppSettings.MinimumUiScalePercent, settings.UiScalePercent);
    }

    [TestMethod]
    public void NormalizeClampsUiScalePercentAboveMaximum()
    {
        var settings = new AppSettings
        {
            UiScalePercent = AppSettings.MaximumUiScalePercent + 1
        };

        settings.Normalize();

        Assert.AreEqual(AppSettings.MaximumUiScalePercent, settings.UiScalePercent);
    }

    [TestMethod]
    public void CloneCopiesUiScalePercent()
    {
        var settings = new AppSettings
        {
            UiScalePercent = 125
        };

        var clone = settings.Clone();

        Assert.AreEqual(settings.UiScalePercent, clone.UiScalePercent);
    }

    [TestMethod]
    public void DiagnosticLoggingEnabledDefaultsToFalse()
    {
        var settings = new AppSettings();

        Assert.IsFalse(settings.DiagnosticLoggingEnabled);
    }

    [TestMethod]
    public void CloneCopiesDiagnosticLoggingEnabled()
    {
        var settings = new AppSettings
        {
            DiagnosticLoggingEnabled = true
        };

        var clone = settings.Clone();

        Assert.IsTrue(clone.DiagnosticLoggingEnabled);
    }
}
