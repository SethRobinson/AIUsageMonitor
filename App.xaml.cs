using System.Globalization;
using AIUsageMonitor.Services;

namespace AIUsageMonitor;

public partial class App : System.Windows.Application
{
    private TrayIconService? _trayIconService;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private AppLogService? _logService;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _logService = new AppLogService();
        RegisterGlobalExceptionHandlers();

        if (TryGetNamedScreenshotOptions(e.Args, "--screenshot-accounts", out var accountsScreenshotPath, out var accountsScreenshotWidth, out var accountsScreenshotHeight))
        {
            try
            {
                ScreenshotService.SaveAccountsScreenshot(accountsScreenshotPath, accountsScreenshotWidth, accountsScreenshotHeight);
                Shutdown();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Could not save accounts screenshot: {exception.Message}");
                Shutdown(1);
            }

            return;
        }

        if (TryGetSettingsScreenshotOptions(e.Args, out var settingsScreenshotPath, out var settingsScreenshotWidth, out var settingsScreenshotHeight))
        {
            try
            {
                ScreenshotService.SaveSettingsScreenshot(settingsScreenshotPath, settingsScreenshotWidth, settingsScreenshotHeight);
                Shutdown();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Could not save settings screenshot: {exception.Message}");
                Shutdown(1);
            }

            return;
        }

        if (TryGetScreenshotOptions(e.Args, out var screenshotPath, out var screenshotWidth, out var screenshotHeight))
        {
            try
            {
                ScreenshotService.SaveOverlayScreenshot(screenshotPath, screenshotWidth, screenshotHeight);
                Shutdown();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Could not save screenshot: {exception.Message}");
                Shutdown(1);
            }

            return;
        }

        var logService = _logService;
        _singleInstanceCoordinator = new SingleInstanceCoordinator(AppMetadata.StartupEntryName);
        if (!_singleInstanceCoordinator.IsPrimaryInstance)
        {
            if (!_singleInstanceCoordinator.SignalExistingInstance())
            {
                logService.Warning("Startup", "Another instance is already running, but it could not be signaled.");
            }

            _singleInstanceCoordinator.Dispose();
            _singleInstanceCoordinator = null;
            Shutdown();
            return;
        }

        var settingsService = new AppSettingsService();
        _trayIconService = new TrayIconService(new UsageAggregatorService(logService, settingsService), settingsService, logService);
        _singleInstanceCoordinator.StartListening(HandleSingleInstanceCommand);
        _trayIconService.ShowOverlay();
    }

    private void HandleSingleInstanceCommand(string command)
    {
        if (!string.Equals(command, SingleInstanceCoordinator.ShowCenterCommand, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => _trayIconService?.ShowOverlay(centerOnPrimaryScreen: true)));
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // UI-thread (dispatcher) exceptions: log and keep running. This is the channel the
        // PenIMC tablet-enumeration crash came through, so a handler here turns that class of
        // failure into a logged warning instead of a process-killing "stopped working" dialog.
        DispatcherUnhandledException += (_, args) =>
        {
            _logService?.Error("UnhandledException", $"Dispatcher: {args.Exception}");
            args.Handled = true;
        };

        // Background-thread faults: the runtime still tears the process down, but capture the
        // detail first so there is a record beyond the Windows event log.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _logService?.Error("UnhandledException", $"AppDomain (terminating={args.IsTerminating}): {exception}");
            }
        };

        // Faulted tasks whose exception was never observed: log and mark observed so they do
        // not escalate to a process-level crash.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logService?.Warning("UnhandledException", $"Unobserved task: {args.Exception}");
            args.SetObserved();
        };
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        _singleInstanceCoordinator?.Dispose();
        base.OnExit(e);
    }

    private static bool TryGetScreenshotOptions(string[] args, out string path, out double? width, out double? height)
    {
        path = string.Empty;
        width = null;
        height = null;

        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--screenshot", StringComparison.OrdinalIgnoreCase))
            {
                path = args[index + 1];
                index++;
                continue;
            }

            if (string.Equals(args[index], "--screenshot-size", StringComparison.OrdinalIgnoreCase) &&
                TryParseScreenshotSize(args[index + 1], out var parsedWidth, out var parsedHeight))
            {
                width = parsedWidth;
                height = parsedHeight;
                index++;
                continue;
            }

            if (string.Equals(args[index], "--screenshot-width", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWidthOnly))
            {
                width = parsedWidthOnly;
                index++;
                continue;
            }

            if (string.Equals(args[index], "--screenshot-height", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeightOnly))
            {
                height = parsedHeightOnly;
                index++;
            }
        }

        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryGetSettingsScreenshotOptions(string[] args, out string path, out double? width, out double? height)
    {
        return TryGetNamedScreenshotOptions(args, "--screenshot-settings", out path, out width, out height);
    }

    private static bool TryGetNamedScreenshotOptions(string[] args, string optionName, out string path, out double? width, out double? height)
    {
        path = string.Empty;
        width = null;
        height = null;

        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                path = args[index + 1];
                index++;
                continue;
            }

            if (string.Equals(args[index], "--screenshot-size", StringComparison.OrdinalIgnoreCase) &&
                TryParseScreenshotSize(args[index + 1], out var parsedWidth, out var parsedHeight))
            {
                width = parsedWidth;
                height = parsedHeight;
                index++;
            }
        }

        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryParseScreenshotSize(string value, out double width, out double height)
    {
        width = 0;
        height = 0;
        var separatorIndex = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (separatorIndex <= 0 || separatorIndex >= value.Length - 1)
        {
            return false;
        }

        return double.TryParse(value[..separatorIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out width) &&
               double.TryParse(value[(separatorIndex + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out height);
    }
}
