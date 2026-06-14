using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class OpenAiCodexLauncherServiceTests
{
    [TestMethod]
    public void SelectLaunchTargetPrefersCliWhenBothCliAndAppAreAvailable()
    {
        var cliPath = @"C:\Users\Test\AppData\Roaming\npm\codex.cmd";

        var target = OpenAiCodexLauncherService.SelectLaunchTarget(cliPath, isCodexAppRegistered: true);

        Assert.IsNotNull(target);
        Assert.AreEqual(OpenAiCodexLaunchTargetKind.Cli, target.Kind);
        Assert.AreEqual(cliPath, target.Value);
        Assert.AreEqual("Codex CLI", target.DisplayName);
    }

    [TestMethod]
    public void SelectLaunchTargetFallsBackToWindowsAppWhenCliIsMissing()
    {
        var target = OpenAiCodexLauncherService.SelectLaunchTarget(null, isCodexAppRegistered: true);

        Assert.IsNotNull(target);
        Assert.AreEqual(OpenAiCodexLaunchTargetKind.WindowsApp, target.Kind);
        Assert.AreEqual("codex://", target.Value);
        Assert.AreEqual("Codex app", target.DisplayName);
    }

    [TestMethod]
    public void SelectLaunchTargetReturnsNullWhenCliAndAppAreMissing()
    {
        var target = OpenAiCodexLauncherService.SelectLaunchTarget(null, isCodexAppRegistered: false);

        Assert.IsNull(target);
    }
}
