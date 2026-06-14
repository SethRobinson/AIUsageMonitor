using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class AntigravityLauncherServiceTests
{
    [TestMethod]
    public void SelectExecutablePathPrefersCurrentAntigravityAppOverLegacyIde()
    {
        var currentAppPath = @"C:\Users\Test\AppData\Local\Programs\Antigravity\Antigravity.exe";
        var legacyIdePath = @"C:\Users\Test\AppData\Local\Programs\Antigravity IDE\Antigravity IDE.exe";

        var selected = AntigravityLauncherService.SelectExecutablePath(
            [legacyIdePath, currentAppPath],
            _ => true);

        Assert.AreEqual(currentAppPath, selected);
    }

    [TestMethod]
    public void ParseRegistryExecutablePathHandlesQuotedDisplayIconWithIconIndex()
    {
        var selected = AntigravityLauncherService.ParseRegistryExecutablePath(
            @"""C:\Users\Test\AppData\Local\Programs\Antigravity\Antigravity.exe"",0");

        Assert.AreEqual(
            @"C:\Users\Test\AppData\Local\Programs\Antigravity\Antigravity.exe",
            selected);
    }

    [TestMethod]
    public void SelectExecutablePathIgnoresUninstallExecutablesAndMissingPaths()
    {
        var uninstallPath = @"C:\Users\Test\AppData\Local\Programs\Antigravity\Uninstall Antigravity.exe";
        var missingCurrentAppPath = @"C:\Users\Test\AppData\Local\Programs\Antigravity\Antigravity.exe";
        var existingLegacyIdePath = @"C:\Users\Test\AppData\Local\Programs\Antigravity IDE\Antigravity IDE.exe";

        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            uninstallPath,
            existingLegacyIdePath
        };

        var selected = AntigravityLauncherService.SelectExecutablePath(
            [uninstallPath, missingCurrentAppPath, existingLegacyIdePath],
            existingPaths.Contains);

        Assert.AreEqual(existingLegacyIdePath, selected);
    }
}
