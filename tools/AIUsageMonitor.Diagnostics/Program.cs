using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

var exitCode = await DiagnosticsProgram.RunAsync(args);
return exitCode;

internal static class DiagnosticsProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "refresh", StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 2;
        }

        var options = RefreshOptions.Parse(args.Skip(1).ToArray());
        if (options.Live && options.Fake)
        {
            Emit("error", new Dictionary<string, object?>
            {
                ["message"] = "Choose either --fake or --live, not both."
            });
            return 2;
        }

        return options.Live
            ? await RunLiveAsync(options)
            : await RunFakeAsync(options);
    }

    private static async Task<int> RunLiveAsync(RefreshOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var settingsService = CreateLiveSettingsService(options);
        var aggregator = new UsageAggregatorService(new AppLogService(), settingsService);
        var result = await RunRefreshAsync(aggregator, "live", options, cts.Token);
        EmitSummary("live", options, [result], result.Canceled ? 1 : 0);
        return result.Canceled ? 1 : 0;
    }

    private static async Task<int> RunFakeAsync(RefreshOptions options)
    {
        var collectors = BuildFakeCollectors(options.Scenario);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var settingsService = new AppSettingsService(tempDirectory);
        settingsService.Save(new AppSettings
        {
            EnabledProviders = collectors.ToDictionary(
                collector => collector.ProviderName,
                _ => true,
                StringComparer.OrdinalIgnoreCase)
        });

        var aggregator = new UsageAggregatorService(new AppLogService(tempDirectory), settingsService, collectors);
        var results = new List<RefreshRunResult>();
        var exitCode = 0;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var scenarioCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        if (string.Equals(options.Scenario, "cancel", StringComparison.OrdinalIgnoreCase))
        {
            scenarioCts.CancelAfter(TimeSpan.FromMilliseconds(options.CancelAfterMilliseconds));
        }

        results.Add(await RunRefreshAsync(aggregator, "fake-1", options, scenarioCts.Token));

        if (string.Equals(options.Scenario, "failure", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(await RunRefreshAsync(aggregator, "fake-2-backoff", options, timeoutCts.Token));
        }

        if (options.AssertIndependent && !AssertIndependent(results.FirstOrDefault()))
        {
            exitCode = 1;
        }

        if (results.Any(result => result.Canceled) &&
            !string.Equals(options.Scenario, "cancel", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = 1;
        }

        EmitSummary("fake", options, results, exitCode);
        return exitCode;
    }

    private static AppSettingsService CreateLiveSettingsService(RefreshOptions options)
    {
        var settingsService = new AppSettingsService();
        if (string.IsNullOrWhiteSpace(options.Provider) && string.IsNullOrWhiteSpace(options.Account))
        {
            return settingsService;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var filteredSettingsService = new AppSettingsService(tempDirectory);
        var settings = settingsService.Load();

        if (!string.IsNullOrWhiteSpace(options.Provider))
        {
            // --provider matches the base provider, so all of its enabled accounts run.
            foreach (var providerName in KnownProviders.All)
            {
                settings.SetProviderEnabled(
                    providerName,
                    string.Equals(providerName, options.Provider, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Account))
        {
            // --account narrows to one account by id or label.
            foreach (var account in settings.ProviderAccounts)
            {
                account.Enabled =
                    string.Equals(account.Id, options.Account, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(account.Label, options.Account, StringComparison.OrdinalIgnoreCase);
            }
        }

        filteredSettingsService.Save(settings);
        return filteredSettingsService;
    }

    private static async Task<RefreshRunResult> RunRefreshAsync(
        UsageAggregatorService aggregator,
        string phase,
        RefreshOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var providers = aggregator.ProviderNames;
        var results = new List<ProviderResultEvent>();
        var canceled = false;

        Emit("start", new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["scenario"] = options.Scenario,
            ["provider"] = options.Provider,
            ["forceRefresh"] = options.ForceRefresh,
            ["providerCount"] = providers.Count
        });

        foreach (var providerName in providers)
        {
            Emit("provider-checking", new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["provider"] = providerName,
                ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds
            });
        }

        try
        {
            await foreach (var provider in aggregator.CollectIncrementalAsync(options.ForceRefresh, cancellationToken))
            {
                var providerResult = new ProviderResultEvent(
                    provider.Name,
                    provider.PlanName,
                    stopwatch.ElapsedMilliseconds,
                    provider.IsUnavailable,
                    provider.StatusMessage);
                results.Add(providerResult);

                Emit("provider-result", new Dictionary<string, object?>
                {
                    ["phase"] = phase,
                    ["provider"] = providerResult.Provider,
                    ["planName"] = providerResult.PlanName,
                    ["elapsedMilliseconds"] = providerResult.ElapsedMilliseconds,
                    ["isUnavailable"] = providerResult.IsUnavailable,
                    ["statusMessage"] = providerResult.StatusMessage
                });
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            Emit("canceled", new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds,
                ["completedProviderCount"] = results.Count
            });
        }

        stopwatch.Stop();
        Emit("complete", new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds,
            ["resultCount"] = results.Count,
            ["canceled"] = canceled
        });

        return new RefreshRunResult(results, stopwatch.ElapsedMilliseconds, canceled);
    }

    private static bool AssertIndependent(RefreshRunResult? result)
    {
        if (result is null)
        {
            return false;
        }

        var fast = result.ProviderResults.FirstOrDefault(provider =>
            string.Equals(provider.Provider, "Fast", StringComparison.OrdinalIgnoreCase));
        var slow = result.ProviderResults.FirstOrDefault(provider =>
            string.Equals(provider.Provider, "Slow", StringComparison.OrdinalIgnoreCase));

        return fast is not null &&
            slow is not null &&
            fast.ElapsedMilliseconds < slow.ElapsedMilliseconds &&
            fast.ElapsedMilliseconds < 600;
    }

    private static IReadOnlyList<IUsageCollector> BuildFakeCollectors(string scenario)
    {
        return scenario.ToLowerInvariant() switch
        {
            "failure" =>
            [
                ScriptedUsageCollector.Success("Fast", TimeSpan.FromMilliseconds(100), 18),
                ScriptedUsageCollector.Failure("Fails", TimeSpan.FromMilliseconds(250))
            ],
            "cancel" =>
            [
                ScriptedUsageCollector.Success("Fast", TimeSpan.FromMilliseconds(100), 18),
                ScriptedUsageCollector.Success("Slow", TimeSpan.FromSeconds(5), 65)
            ],
            _ =>
            [
                ScriptedUsageCollector.Success("Fast", TimeSpan.FromMilliseconds(100), 18),
                ScriptedUsageCollector.Success("Medium", TimeSpan.FromMilliseconds(350), 42),
                ScriptedUsageCollector.Success("Slow", TimeSpan.FromMilliseconds(900), 65)
            ]
        };
    }

    private static void EmitSummary(
        string mode,
        RefreshOptions options,
        IReadOnlyList<RefreshRunResult> results,
        int exitCode)
    {
        Emit("summary", new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["scenario"] = options.Scenario,
            ["provider"] = options.Provider,
            ["forceRefresh"] = options.ForceRefresh,
            ["runCount"] = results.Count,
            ["resultCount"] = results.Sum(result => result.ProviderResults.Count),
            ["canceled"] = results.Any(result => result.Canceled),
            ["elapsedMilliseconds"] = results.Sum(result => result.ElapsedMilliseconds),
            ["independentAssertion"] = options.AssertIndependent ? AssertIndependent(results.FirstOrDefault()) : null,
            ["exitCode"] = exitCode
        });
    }

    private static void Emit(string eventName, IDictionary<string, object?> values)
    {
        values["event"] = eventName;
        values["at"] = DateTimeOffset.Now;
        Console.WriteLine(JsonSerializer.Serialize(values, JsonOptions));
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  AIUsageMonitor.Diagnostics refresh --fake --scenario staggered --assert-independent");
        Console.Error.WriteLine("  AIUsageMonitor.Diagnostics refresh --fake --scenario failure");
        Console.Error.WriteLine("  AIUsageMonitor.Diagnostics refresh --fake --scenario cancel --cancel-after-ms 500");
        Console.Error.WriteLine("  AIUsageMonitor.Diagnostics refresh --live --timeout-seconds 120");
        Console.Error.WriteLine("  AIUsageMonitor.Diagnostics refresh --live --provider Anthropic --force-refresh --timeout-seconds 120");
        Console.Error.WriteLine("  AIUsageMonitor.Diagnostics refresh --live --provider Anthropic --account Work --timeout-seconds 120");
    }
}

internal sealed record RefreshOptions(
    bool Fake,
    bool Live,
    string Scenario,
    string Provider,
    string Account,
    bool ForceRefresh,
    bool AssertIndependent,
    int CancelAfterMilliseconds,
    int TimeoutSeconds)
{
    public static RefreshOptions Parse(string[] args)
    {
        var fake = false;
        var live = false;
        var scenario = "staggered";
        var provider = string.Empty;
        var account = string.Empty;
        var forceRefresh = false;
        var assertIndependent = false;
        var cancelAfterMilliseconds = 500;
        var timeoutSeconds = 120;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--fake":
                    fake = true;
                    break;
                case "--live":
                    live = true;
                    break;
                case "--scenario" when index + 1 < args.Length:
                    scenario = args[++index];
                    break;
                case "--provider" when index + 1 < args.Length:
                    provider = args[++index];
                    break;
                case "--account" when index + 1 < args.Length:
                    account = args[++index];
                    break;
                case "--force-refresh":
                    forceRefresh = true;
                    break;
                case "--assert-independent":
                    assertIndependent = true;
                    break;
                case "--cancel-after-ms" when index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedCancel):
                    cancelAfterMilliseconds = Math.Max(1, parsedCancel);
                    index++;
                    break;
                case "--timeout-seconds" when index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedTimeout):
                    timeoutSeconds = Math.Max(1, parsedTimeout);
                    index++;
                    break;
            }
        }

        if (!live)
        {
            fake = true;
        }

        return new RefreshOptions(fake, live, scenario, provider, account, forceRefresh, assertIndependent, cancelAfterMilliseconds, timeoutSeconds);
    }
}

internal sealed record ProviderResultEvent(
    string Provider,
    string PlanName,
    long ElapsedMilliseconds,
    bool IsUnavailable,
    string StatusMessage);

internal sealed record RefreshRunResult(
    IReadOnlyList<ProviderResultEvent> ProviderResults,
    long ElapsedMilliseconds,
    bool Canceled);

internal sealed class ScriptedUsageCollector : IUsageCollector
{
    private readonly TimeSpan _delay;
    private readonly double _usedPercent;
    private readonly Exception? _exception;

    private ScriptedUsageCollector(string providerName, TimeSpan delay, double usedPercent, Exception? exception)
    {
        ProviderName = providerName;
        _delay = delay;
        _usedPercent = usedPercent;
        _exception = exception;
    }

    public string ProviderName { get; }

    public static ScriptedUsageCollector Success(string providerName, TimeSpan delay, double usedPercent)
    {
        return new ScriptedUsageCollector(providerName, delay, usedPercent, null);
    }

    public static ScriptedUsageCollector Failure(string providerName, TimeSpan delay)
    {
        return new ScriptedUsageCollector(
            providerName,
            delay,
            100,
            new HttpRequestException("Scripted provider failure."));
    }

    public async Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
    {
        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, cancellationToken);
        }

        if (_exception is not null)
        {
            throw _exception;
        }

        return new ProviderUsage
        {
            Name = ProviderName,
            Source = "Scripted fake collector",
            StatusMessage = $"{ProviderName} scripted usage.",
            Windows =
            [
                new UsageWindow
                {
                    Title = "Scripted",
                    Limit = 100,
                    Used = _usedPercent,
                    Remaining = Math.Max(0, 100 - _usedPercent),
                    ResetAt = DateTimeOffset.Now.AddHours(1)
                }
            ]
        };
    }
}
