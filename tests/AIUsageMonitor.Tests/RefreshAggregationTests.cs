using System.Diagnostics;
using System.IO;
using System.Net.Http;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class RefreshAggregationTests
{
    [TestMethod]
    public async Task FastProviderResultIsEmittedBeforeSlowProviderResult()
    {
        var fast = ScriptedUsageCollector.Success("Fast", TimeSpan.FromMilliseconds(75), 10);
        var slow = ScriptedUsageCollector.Success("Slow", TimeSpan.FromMilliseconds(600), 50);
        var aggregator = CreateAggregator(fast, slow);

        var results = await CollectResultsAsync(aggregator);

        Assert.AreEqual("Fast", results[0].Name);
        Assert.AreEqual("Slow", results[1].Name);
    }

    [TestMethod]
    public async Task ParallelRefreshElapsedTimeTracksSlowestProviderNotSum()
    {
        var first = ScriptedUsageCollector.Success("First", TimeSpan.FromMilliseconds(500), 10);
        var second = ScriptedUsageCollector.Success("Second", TimeSpan.FromMilliseconds(500), 50);
        var aggregator = CreateAggregator(first, second);
        var stopwatch = Stopwatch.StartNew();

        var results = await CollectResultsAsync(aggregator);

        stopwatch.Stop();
        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(
            stopwatch.ElapsedMilliseconds < 850,
            $"Expected parallel collection to finish well under the sequential 1000ms path, got {stopwatch.ElapsedMilliseconds}ms.");
    }

    [TestMethod]
    public async Task ProviderFailureReturnsUnavailableResultWithoutBlockingSuccessfulProvider()
    {
        var fast = ScriptedUsageCollector.Success("Fast", TimeSpan.FromMilliseconds(75), 10);
        var failing = ScriptedUsageCollector.Failure("Fails", TimeSpan.FromMilliseconds(500));
        var aggregator = CreateAggregator(fast, failing);

        var results = await CollectResultsAsync(aggregator);

        Assert.AreEqual("Fast", results[0].Name);
        var failedProvider = results.Single(provider => provider.Name == "Fails");
        Assert.IsTrue(failedProvider.IsUnavailable);
        StringAssert.Contains(failedProvider.StatusMessage, "Collection failed");
    }

    [TestMethod]
    public async Task BackoffIsTrackedPerProviderAndManualResetClearsIt()
    {
        var failing = ScriptedUsageCollector.Failure("Fails", TimeSpan.Zero);
        var aggregator = CreateAggregator(failing);

        var firstResults = await CollectResultsAsync(aggregator);
        var secondResults = await CollectResultsAsync(aggregator);

        Assert.AreEqual(1, failing.CallCount);
        Assert.IsTrue(firstResults.Single().IsUnavailable);
        StringAssert.Contains(secondResults.Single().StatusMessage, "Collection paused");

        aggregator.ResetBackoff();
        _ = await CollectResultsAsync(aggregator);

        Assert.AreEqual(2, failing.CallCount);
    }

    [TestMethod]
    public async Task ForceRefreshIsPassedToAwareCollectors()
    {
        var forceAware = new ForceAwareUsageCollector("Aware");
        var aggregator = CreateAggregator(forceAware);

        _ = await CollectResultsAsync(aggregator);
        _ = await CollectResultsAsync(aggregator, forceRefresh: true);

        CollectionAssert.AreEqual(new[] { false, true }, forceAware.ForceRefreshValues);
    }

    [TestMethod]
    public async Task CancellationStopsPendingCollectorsWithoutDiscardingCompletedResults()
    {
        var fast = ScriptedUsageCollector.Success("Fast", TimeSpan.FromMilliseconds(100), 10);
        var slow = ScriptedUsageCollector.Success("Slow", TimeSpan.FromSeconds(5), 50);
        var aggregator = CreateAggregator(fast, slow);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var results = new List<ProviderUsage>();
        var canceled = false;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await foreach (var provider in aggregator.CollectIncrementalAsync(cts.Token))
            {
                results.Add(provider);
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        stopwatch.Stop();
        Assert.IsTrue(canceled);
        Assert.IsTrue(results.Any(provider => provider.Name == "Fast"));
        Assert.IsFalse(results.Any(provider => provider.Name == "Slow"));
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 2000);
    }

    [TestMethod]
    public void ViewModelReplacesOneProviderWhileOtherProvidersRemainChecking()
    {
        var viewModel = new UsageOverlayViewModel();
        viewModel.SetChecking(["Fast", "Slow"]);

        viewModel.ApplyProvider(BuildUsage("Fast", 10));

        var fast = viewModel.Providers.Single(provider => provider.ShortName == "Fast");
        var slow = viewModel.Providers.Single(provider => provider.ShortName == "Slow");
        Assert.IsFalse(fast.IsChecking);
        Assert.IsTrue(slow.IsChecking);
        Assert.IsFalse(fast.SummaryText.Contains("checking", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(slow.SummaryText, "checking");
    }

    [TestMethod]
    public async Task SynchronousCollectorWorkDoesNotBlockMoveNextCaller()
    {
        var blocking = ScriptedUsageCollector.Success(
            "Blocking",
            TimeSpan.Zero,
            10,
            synchronousBlock: TimeSpan.FromMilliseconds(800));
        var aggregator = CreateAggregator(blocking);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var enumerator = aggregator.CollectIncrementalAsync(cts.Token).GetAsyncEnumerator();

        var stopwatch = Stopwatch.StartNew();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        var moveNextReturnedAt = stopwatch.ElapsedMilliseconds;

        Assert.IsTrue(
            moveNextReturnedAt < 200,
            $"MoveNextAsync returned after {moveNextReturnedAt}ms, which means synchronous collector work ran on the caller thread.");
        Assert.IsFalse(moveNextTask.IsCompleted);
        Assert.IsTrue(await moveNextTask);
        Assert.AreEqual("Blocking", enumerator.Current.Name);
    }

    private static UsageAggregatorService CreateAggregator(params IUsageCollector[] collectors)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var settingsService = new AppSettingsService(tempDirectory);
        settingsService.Save(new AppSettings
        {
            EnabledProviders = collectors.ToDictionary(
                collector => collector.ProviderName,
                _ => true,
                StringComparer.OrdinalIgnoreCase)
        });

        return new UsageAggregatorService(
            new AppLogService(tempDirectory),
            settingsService,
            collectors.ToList());
    }

    private static async Task<List<ProviderUsage>> CollectResultsAsync(
        UsageAggregatorService aggregator,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ProviderUsage>();
        await foreach (var provider in aggregator.CollectIncrementalAsync(forceRefresh, cancellationToken))
        {
            results.Add(provider);
        }

        return results;
    }

    private static ProviderUsage BuildUsage(string providerName, double usedPercent)
    {
        return new ProviderUsage
        {
            Name = providerName,
            Source = "Test",
            StatusMessage = "Test usage.",
            LastCheckedAt = DateTimeOffset.Now,
            Windows =
            [
                new UsageWindow
                {
                    Title = "Test",
                    Limit = 100,
                    Used = usedPercent,
                    Remaining = Math.Max(0, 100 - usedPercent),
                    ResetAt = DateTimeOffset.Now.AddHours(1)
                }
            ]
        };
    }

    private sealed class ScriptedUsageCollector : IUsageCollector
    {
        private readonly TimeSpan _delay;
        private readonly TimeSpan _synchronousBlock;
        private readonly double _usedPercent;
        private readonly Exception? _exception;
        private int _callCount;

        private ScriptedUsageCollector(
            string providerName,
            TimeSpan delay,
            double usedPercent,
            Exception? exception,
            TimeSpan synchronousBlock)
        {
            ProviderName = providerName;
            _delay = delay;
            _usedPercent = usedPercent;
            _exception = exception;
            _synchronousBlock = synchronousBlock;
        }

        public string ProviderName { get; }

        public int CallCount => _callCount;

        public static ScriptedUsageCollector Success(
            string providerName,
            TimeSpan delay,
            double usedPercent,
            TimeSpan synchronousBlock = default)
        {
            return new ScriptedUsageCollector(providerName, delay, usedPercent, null, synchronousBlock);
        }

        public static ScriptedUsageCollector Failure(string providerName, TimeSpan delay)
        {
            return new ScriptedUsageCollector(
                providerName,
                delay,
                100,
                new HttpRequestException("Scripted provider failure."),
                default);
        }

        public async Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);

            if (_synchronousBlock > TimeSpan.Zero)
            {
                Thread.Sleep(_synchronousBlock);
            }

            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return BuildUsage(ProviderName, _usedPercent);
        }
    }

    private sealed class ForceAwareUsageCollector(string providerName) : IForceRefreshUsageCollector
    {
        private readonly List<bool> _forceRefreshValues = [];

        public string ProviderName { get; } = providerName;

        public bool[] ForceRefreshValues => _forceRefreshValues.ToArray();

        public Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
        {
            return CollectAsync(forceRefresh: false, cancellationToken);
        }

        public Task<ProviderUsage> CollectAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            _forceRefreshValues.Add(forceRefresh);
            return Task.FromResult(BuildUsage(ProviderName, 10));
        }
    }
}
