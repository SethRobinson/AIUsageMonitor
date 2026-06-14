using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class SingleInstanceCoordinatorTests
{
    [TestMethod]
    public void FirstCoordinatorOwnsInstanceAndSecondDoesNot()
    {
        var instanceName = CreateInstanceName();

        using var primary = new SingleInstanceCoordinator(instanceName);
        using var secondary = new SingleInstanceCoordinator(instanceName);

        Assert.IsTrue(primary.IsPrimaryInstance);
        Assert.IsFalse(secondary.IsPrimaryInstance);
    }

    [TestMethod]
    public async Task SecondaryCoordinatorSignalsPrimaryCoordinator()
    {
        var instanceName = CreateInstanceName();
        var receivedCommand = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var primary = new SingleInstanceCoordinator(instanceName);
        primary.StartListening(command => receivedCommand.TrySetResult(command));

        using var secondary = new SingleInstanceCoordinator(instanceName);

        Assert.IsFalse(secondary.IsPrimaryInstance);
        Assert.IsTrue(secondary.SignalExistingInstance(SingleInstanceCoordinator.ShowCenterCommand, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(
            SingleInstanceCoordinator.ShowCenterCommand,
            await receivedCommand.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static string CreateInstanceName()
    {
        return $"AIUsageMonitor.Tests.{Guid.NewGuid():N}";
    }
}
