using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AIUsageMonitor.Models;
using AIUsageMonitor.ViewModels;
using AIUsageMonitor.Views;
using WinForms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace AIUsageMonitor.Services;

public sealed class TrayIconService : IDisposable
{
    private const int WindowPlacementSaveDelayMilliseconds = 500;

    private readonly UsageAggregatorService _usageAggregatorService;
    private readonly AppSettingsService _settingsService;
    private readonly AppLogService _logService;
    private readonly ClaudeStatusExporterService _claudeStatusExporterService;
    private readonly UsageOverlayViewModel _viewModel = new();
    private readonly Icon _appIcon;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _relativeTimeTimer = new();
    private readonly DispatcherTimer _windowPlacementSaveTimer = new();
    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    private AppSettings _settings;
    private UsageOverlayWindow? _overlayWindow;
    private LogWindow? _logWindow;
    private CursorDashboardLoginWindow? _cursorDashboardLoginWindow;
    private CancellationTokenSource? _refreshCts;
    private bool _suppressNextCancellationLog;
    private bool _disposed;

    public TrayIconService(UsageAggregatorService usageAggregatorService, AppSettingsService settingsService, AppLogService logService)
    {
        _usageAggregatorService = usageAggregatorService;
        _settingsService = settingsService;
        _logService = logService;
        _claudeStatusExporterService = new ClaudeStatusExporterService(logService);
        _appIcon = AppIconService.LoadTrayIcon();
        _settings = _settingsService.Load();
        EnsureClaudeStatusExporterIfEnabled();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show Seth's AI Usage Monitor", null, (_, _) => Dispatch(ShowOverlay));
        menu.Items.Add("Refresh Now", null, (_, _) => Dispatch(() => _ = ManualRefreshAsync()));
        menu.Items.Add("Settings", null, (_, _) => Dispatch(ShowSettings));
        menu.Items.Add("Logs", null, (_, _) => Dispatch(ShowLogs));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatch(Exit));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _appIcon,
            Text = AppMetadata.DisplayNameWithVersion,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.MouseUp += NotifyIconOnMouseUp;

        _refreshTimer.Tick += RefreshTimerOnTick;
        _relativeTimeTimer.Interval = TimeSpan.FromSeconds(30);
        _relativeTimeTimer.Tick += RelativeTimeTimerOnTick;
        _relativeTimeTimer.Start();
        _windowPlacementSaveTimer.Interval = TimeSpan.FromMilliseconds(WindowPlacementSaveDelayMilliseconds);
        _windowPlacementSaveTimer.Tick += WindowPlacementSaveTimerOnTick;
        ConfigureRefreshTimer();
        _viewModel.SetAutoRefreshInterval(_settings.UpdateIntervalMinutes);
        UpdateLogSummary();
        _ = RefreshUsageAsync(force: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _windowPlacementSaveTimer.Stop();
        SaveOverlayWindowPlacement();
        _refreshCts?.Cancel();
        _refreshCts = null;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimerOnTick;
        _relativeTimeTimer.Stop();
        _relativeTimeTimer.Tick -= RelativeTimeTimerOnTick;
        _windowPlacementSaveTimer.Tick -= WindowPlacementSaveTimerOnTick;
        _notifyIcon.MouseUp -= NotifyIconOnMouseUp;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
    }

    private void NotifyIconOnMouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            Dispatch(ToggleOverlay);
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = WpfApplication.Current.Dispatcher;

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private void ToggleOverlay()
    {
        if (_overlayWindow?.IsVisible == true)
        {
            SaveOverlayWindowPlacement();
            _overlayWindow.Hide();
            return;
        }

        ShowOverlay();
    }

    public void ShowOverlay()
    {
        ShowOverlay(centerOnPrimaryScreen: false);
    }

    public void ShowOverlay(bool centerOnPrimaryScreen)
    {
        EnsureOverlayWindow();
        if (centerOnPrimaryScreen)
        {
            _overlayWindow!.CenterOnPrimaryScreen();
        }
        else
        {
            _overlayWindow!.EnsureValidPlacement();
        }

        _overlayWindow!.Show();
        _overlayWindow.Activate();
        _overlayWindow.Focus();
        _overlayWindow.EnsureTopmost();
    }

    private void EnsureOverlayWindow()
    {
        if (_overlayWindow is not null)
        {
            return;
        }

        _overlayWindow = new UsageOverlayWindow
        {
            DataContext = _viewModel
        };
        _overlayWindow.ApplyStartupPlacement(_settings.OverlayWindowPlacement);
        _overlayWindow.ReloadRequested += (_, _) => _ = ManualRefreshAsync();
        _overlayWindow.SettingsRequested += (_, _) => ShowSettings();
        _overlayWindow.LogsRequested += (_, _) => ShowLogs();
        _overlayWindow.ExitRequested += (_, _) => Exit();
        _overlayWindow.LocationChanged += OverlayWindowOnLocationChanged;
        _overlayWindow.SizeChanged += OverlayWindowOnSizeChanged;
        _overlayWindow.IsVisibleChanged += OverlayWindowOnIsVisibleChanged;
        _overlayWindow.Closed += OverlayWindowOnClosed;
    }

    private void OverlayWindowOnLocationChanged(object? sender, EventArgs e)
    {
        QueueOverlayWindowPlacementSave();
    }

    private void OverlayWindowOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueOverlayWindowPlacementSave();
    }

    private void OverlayWindowOnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            SaveOverlayWindowPlacement();
        }
    }

    private void OverlayWindowOnClosed(object? sender, EventArgs e)
    {
        if (sender is UsageOverlayWindow window)
        {
            SaveOverlayWindowPlacement(window);
            window.LocationChanged -= OverlayWindowOnLocationChanged;
            window.SizeChanged -= OverlayWindowOnSizeChanged;
            window.IsVisibleChanged -= OverlayWindowOnIsVisibleChanged;
            window.Closed -= OverlayWindowOnClosed;
        }

        _overlayWindow = null;
    }

    private void QueueOverlayWindowPlacementSave()
    {
        if (_disposed || _overlayWindow is null)
        {
            return;
        }

        _windowPlacementSaveTimer.Stop();
        _windowPlacementSaveTimer.Start();
    }

    private void WindowPlacementSaveTimerOnTick(object? sender, EventArgs e)
    {
        _windowPlacementSaveTimer.Stop();
        SaveOverlayWindowPlacement();
    }

    private void SaveOverlayWindowPlacement()
    {
        if (_overlayWindow is not null)
        {
            SaveOverlayWindowPlacement(_overlayWindow);
        }
    }

    private void SaveOverlayWindowPlacement(UsageOverlayWindow window)
    {
        try
        {
            _settings.OverlayWindowPlacement = window.GetCurrentPlacement();
            _settingsService.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logService.Warning("Settings", $"Could not save window placement: {ex.Message}");
        }
    }

    private async Task ManualRefreshAsync()
    {
        await RefreshUsageAsync(force: true, resetBackoff: true);
        RestartRefreshTimer();
    }

    private void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        _ = RefreshUsageAsync(force: false);
    }

    private void RelativeTimeTimerOnTick(object? sender, EventArgs e)
    {
        _viewModel.RefreshRelativeTimes();
    }

    private void ConfigureRefreshTimer()
    {
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromMinutes(_settings.UpdateIntervalMinutes);
        _refreshTimer.Start();
    }

    private void RestartRefreshTimer()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void ShowSettings()
    {
        var settingsWindow = new SettingsWindow(_settings, _settingsService, _logService);

        if (_overlayWindow?.IsVisible == true)
        {
            settingsWindow.Owner = _overlayWindow;
        }
        else
        {
            settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (settingsWindow.ShowDialog() != true)
        {
            return;
        }

        _settings = settingsWindow.Settings;
        _settingsService.Save(_settings);
        ApplyAutoRunSetting();
        EnsureClaudeStatusExporterIfEnabled();
        ConfigureRefreshTimer();
        _viewModel.SetAutoRefreshInterval(_settings.UpdateIntervalMinutes);
        _logService.Info("Settings", "Settings saved.");
        UpdateLogSummary();

        _ = ManualRefreshAsync();
    }

    private void ApplyAutoRunSetting()
    {
        try
        {
            AutoRunService.SetEnabled(_settings.AutoRunAtLoginEnabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logService.Warning("Settings", $"Could not update auto-run setting: {ex.Message}");
        }
    }

    private void EnsureClaudeStatusExporterIfEnabled()
    {
        if (!_settings.IsProviderEnabled(KnownProviders.Anthropic) || !_settings.ClaudeStatusExporterEnabled)
        {
            return;
        }

        try
        {
            _claudeStatusExporterService.EnsureInstalled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            _logService.Warning("Anthropic", $"Could not install Claude status exporter: {ex.Message}");
        }
    }

    private async Task RefreshUsageAsync(bool force, bool resetBackoff = false)
    {
        if (_disposed)
        {
            return;
        }

        if (force && _refreshCts is not null)
        {
            _suppressNextCancellationLog = true;
            _refreshCts.Cancel();
        }

        var lockAcquired = force
            ? await WaitForRefreshLockAsync()
            : await _refreshSemaphore.WaitAsync(0);

        if (!lockAcquired)
        {
            return;
        }

        using var refreshCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _refreshCts = refreshCts;

        try
        {
            if (resetBackoff)
            {
                _usageAggregatorService.ResetBackoff();
            }

            _viewModel.SetChecking(_usageAggregatorService.ProviderNames);
            await foreach (var provider in _usageAggregatorService.CollectIncrementalAsync(refreshCts.Token))
            {
                if (_disposed)
                {
                    return;
                }

                _viewModel.ApplyProvider(provider, "Live/local collectors");
                UpdateLogSummary();
            }

            _viewModel.SetSnapshotMetadata(DateTimeOffset.Now, "Live/local collectors");
            UpdateLogSummary();
        }
        catch (OperationCanceledException ex)
        {
            if (!_disposed && !_suppressNextCancellationLog)
            {
                _logService.Warning("Refresh", $"Usage collection timed out or was canceled: {ex.Message}");
                _viewModel.ClearChecking();
                _viewModel.SetError("Usage collection timed out. Details were added to the log.", "Live/local collectors");
                UpdateLogSummary();
            }

            _suppressNextCancellationLog = false;
        }
        catch (Exception ex)
        {
            _logService.Error("Refresh", $"{ex.GetType().Name}: {ex.Message}");
            _viewModel.ClearChecking();
            _viewModel.SetError("Usage collection failed. Details were added to the log.", "Live/local collectors");
            UpdateLogSummary();
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, refreshCts))
            {
                _refreshCts = null;
            }

            _viewModel.RefreshRelativeTimes();
            _refreshSemaphore.Release();
        }
    }

    private async Task<bool> WaitForRefreshLockAsync()
    {
        try
        {
            await _refreshSemaphore.WaitAsync();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ShowLogs()
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow(_logService);
            if (_overlayWindow?.IsVisible == true)
            {
                _logWindow.Owner = _overlayWindow;
            }

            _logWindow.LogsCleared += (_, _) => UpdateLogSummary();
            _logWindow.Closed += (_, _) =>
            {
                _logWindow = null;
                UpdateLogSummary();
            };
        }

        _logWindow.Show();
        _logWindow.Activate();
        UpdateLogSummary();
    }

    private void ShowCursorDashboardLogin()
    {
        if (_cursorDashboardLoginWindow is null)
        {
            _cursorDashboardLoginWindow = new CursorDashboardLoginWindow(_settingsService, _logService);
            if (_overlayWindow?.IsVisible == true)
            {
                _cursorDashboardLoginWindow.Owner = _overlayWindow;
            }

            _cursorDashboardLoginWindow.Closed += (_, _) =>
            {
                _cursorDashboardLoginWindow = null;
                _settings = _settingsService.Load();
                UpdateLogSummary();
                _ = ManualRefreshAsync();
            };
        }

        _cursorDashboardLoginWindow.Show();
        _cursorDashboardLoginWindow.Activate();
    }

    private void UpdateLogSummary()
    {
        _viewModel.SetLogSummary(_logService.RecentErrorCount);
    }

    private void Exit()
    {
        _cursorDashboardLoginWindow?.Close();
        _logWindow?.Close();
        _overlayWindow?.Close();
        Dispose();
        WpfApplication.Current.Shutdown();
    }
}
