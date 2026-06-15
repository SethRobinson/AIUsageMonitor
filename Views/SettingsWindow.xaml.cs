using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfSlider = System.Windows.Controls.Slider;

namespace AIUsageMonitor.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly AppLogService _logService;

    public SettingsWindow(AppSettings settings, AppSettingsService settingsService, AppLogService logService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _logService = logService;
        Settings = settings.Clone();
        Settings.Normalize();
        UpdateIntervalTextBox.Text = Settings.UpdateIntervalMinutes.ToString();
        ConfigureUiScaleSlider();
        UiScaleSlider.Value = Settings.UiScalePercent;
        UpdateUiScaleValueLabel(Settings.UiScalePercent);
        AnthropicProviderEnabledCheckBox.IsChecked = Settings.IsProviderEnabled(KnownProviders.Anthropic);
        OpenAiProviderEnabledCheckBox.IsChecked = Settings.IsProviderEnabled(KnownProviders.OpenAI);
        AntigravityProviderEnabledCheckBox.IsChecked = Settings.IsProviderEnabled(KnownProviders.Antigravity);
        GeminiProviderEnabledCheckBox.IsChecked = Settings.IsProviderEnabled(KnownProviders.Gemini);
        CursorProviderEnabledCheckBox.IsChecked = Settings.IsProviderEnabled(KnownProviders.Cursor);
        UpdateCursorModeSummary();
        ClaudeStatusExporterCheckBox.IsChecked = Settings.ClaudeStatusExporterEnabled;
        AutoRunAtLoginCheckBox.IsChecked = Settings.AutoRunAtLoginEnabled || Services.AutoRunService.IsEnabled();
        DiagnosticLoggingCheckBox.IsChecked = Settings.DiagnosticLoggingEnabled;
        AlwaysOnTopCheckBox.IsChecked = Settings.AlwaysOnTop;
        UpdateIntervalTextBox.SelectAll();
        UpdateIntervalTextBox.Focus();
    }

    public AppSettings Settings { get; private set; }

    public event EventHandler<UiScalePreviewChangedEventArgs>? UiScalePreviewChanged;

    private void SaveButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(UpdateIntervalTextBox.Text.Trim(), out var minutes))
        {
            ValidationTextBlock.Text = "Enter a whole number of minutes.";
            return;
        }

        if (minutes is < AppSettings.MinimumUpdateIntervalMinutes or > AppSettings.MaximumUpdateIntervalMinutes)
        {
            ValidationTextBlock.Text = $"Enter a value from {AppSettings.MinimumUpdateIntervalMinutes} to {AppSettings.MaximumUpdateIntervalMinutes} minutes.";
            return;
        }

        Settings.UpdateIntervalMinutes = minutes;
        Settings.UiScalePercent = GetSelectedUiScalePercent();
        ApplySettingsFromControls();
        DialogResult = true;
    }

    private void CursorSetupButtonOnClick(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromControls();

        var setupWindow = new CursorSetupWindow(Settings, _settingsService, _logService)
        {
            Owner = this
        };

        if (setupWindow.ShowDialog() != true)
        {
            return;
        }

        Settings = setupWindow.Settings;
        UpdateCursorModeSummary();
    }

    private void ProviderSetupButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string providerName })
        {
            return;
        }

        var setupInfo = providerName switch
        {
            KnownProviders.Anthropic => new ProviderSetupInfo(
                "Anthropic Setup",
                "Install Claude Code, run claude, and sign in. Seth's AI Usage Monitor reads local Claude Code OAuth and status-line quota data when available.",
                "Open Claude Code setup",
                "https://docs.claude.com/en/docs/claude-code/setup"),
            KnownProviders.OpenAI => new ProviderSetupInfo(
                "OpenAI Setup",
                "Install the Codex CLI or Codex app, open Codex, and sign in. Seth's AI Usage Monitor reads quota snapshots from local Codex session logs.",
                "Open Codex",
                OpenAiCodexLauncherService.SetupUrl,
                OpenCodex),
            KnownProviders.Antigravity => new ProviderSetupInfo(
                "Antigravity Setup",
                "Run Antigravity and sign in. Seth's AI Usage Monitor reads the local Antigravity language server quota while Antigravity is running.",
                "Open Antigravity",
                AntigravityLauncherService.DownloadUrl,
                OpenAntigravity),
            KnownProviders.Gemini => new ProviderSetupInfo(
                "Gemini Setup",
                "Install Gemini CLI, run gemini, and sign in. Seth's AI Usage Monitor reads Gemini CLI credentials, Code Assist quota, quota status exports, and local session usage.",
                "Open Gemini CLI setup",
                "https://google-gemini.github.io/gemini-cli/docs/get-started/"),
            _ => null
        };

        if (setupInfo is null)
        {
            return;
        }

        var setupWindow = new ProviderSetupInfoWindow(
            setupInfo.Title,
            setupInfo.Message,
            setupInfo.LinkText,
            setupInfo.Url,
            setupInfo.LinkAction)
        {
            Owner = this
        };
        setupWindow.ShowDialog();
    }

    private static void OpenAntigravity(Window owner)
    {
        var result = AntigravityLauncherService.TryLaunch();
        switch (result.Status)
        {
            case AntigravityLaunchStatus.Started:
                return;
            case AntigravityLaunchStatus.NotFound:
                System.Windows.MessageBox.Show(
                    owner,
                    "Could not find Antigravity on this machine. Download and install Antigravity, then try again.",
                    "Antigravity",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            case AntigravityLaunchStatus.Failed:
                System.Windows.MessageBox.Show(
                    owner,
                    $"Could not open Antigravity ({result.ExecutablePath}): {result.Exception?.Message}",
                    "Antigravity",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
        }
    }

    private static void OpenCodex(Window owner)
    {
        var result = OpenAiCodexLauncherService.TryLaunch();
        switch (result.Status)
        {
            case OpenAiCodexLaunchStatus.Started:
                return;
            case OpenAiCodexLaunchStatus.NotFound:
                System.Windows.MessageBox.Show(
                    owner,
                    "Could not find Codex CLI or the Codex app on this machine. Download and install Codex, then try again.",
                    "OpenAI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            case OpenAiCodexLaunchStatus.Failed:
                System.Windows.MessageBox.Show(
                    owner,
                    $"Could not open {result.Target?.DisplayName}: {result.Exception?.Message}",
                    "OpenAI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
        }
    }

    private void AboutButtonOnClick(object sender, RoutedEventArgs e)
    {
        var aboutWindow = new AboutWindow
        {
            Owner = this
        };
        aboutWindow.ShowDialog();
    }

    private void CancelButtonOnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ApplySettingsFromControls()
    {
        Settings.UiScalePercent = GetSelectedUiScalePercent();
        Settings.SetProviderEnabled(KnownProviders.Anthropic, AnthropicProviderEnabledCheckBox.IsChecked == true);
        Settings.SetProviderEnabled(KnownProviders.OpenAI, OpenAiProviderEnabledCheckBox.IsChecked == true);
        Settings.SetProviderEnabled(KnownProviders.Antigravity, AntigravityProviderEnabledCheckBox.IsChecked == true);
        Settings.SetProviderEnabled(KnownProviders.Gemini, GeminiProviderEnabledCheckBox.IsChecked == true);
        Settings.SetProviderEnabled(KnownProviders.Cursor, CursorProviderEnabledCheckBox.IsChecked == true);
        Settings.ClaudeStatusExporterEnabled = ClaudeStatusExporterCheckBox.IsChecked == true;
        Settings.AutoRunAtLoginEnabled = AutoRunAtLoginCheckBox.IsChecked == true;
        Settings.DiagnosticLoggingEnabled = DiagnosticLoggingCheckBox.IsChecked == true;
        Settings.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
        MergeSavedCursorDashboardLogin();
        Settings.Normalize();
    }

    private void ConfigureUiScaleSlider()
    {
        UiScaleSlider.Minimum = AppSettings.MinimumUiScalePercent;
        UiScaleSlider.Maximum = AppSettings.MaximumUiScalePercent;
        UiScaleSlider.TickFrequency = AppSettings.UiScaleStepPercent;
        UiScaleSlider.SmallChange = AppSettings.UiScaleStepPercent;
        UiScaleSlider.LargeChange = AppSettings.UiScaleStepPercent * 2;
    }

    private void UiScaleSliderOnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (UiScaleValueTextBlock is null || sender is not WpfSlider slider)
        {
            return;
        }

        var uiScalePercent = GetUiScalePercent(slider.Value);
        UpdateUiScaleValueLabel(uiScalePercent);
        UiScalePreviewChanged?.Invoke(this, new UiScalePreviewChangedEventArgs(uiScalePercent));
    }

    private int GetSelectedUiScalePercent()
    {
        return GetUiScalePercent(UiScaleSlider.Value);
    }

    private static int GetUiScalePercent(double scaleValue)
    {
        var clampedScale = Math.Clamp(
            scaleValue,
            AppSettings.MinimumUiScalePercent,
            AppSettings.MaximumUiScalePercent);
        var stepCount = (int)Math.Round(
            (clampedScale - AppSettings.MinimumUiScalePercent) / AppSettings.UiScaleStepPercent,
            MidpointRounding.AwayFromZero);
        var steppedScale = AppSettings.MinimumUiScalePercent + stepCount * AppSettings.UiScaleStepPercent;

        return Math.Clamp(
            steppedScale,
            AppSettings.MinimumUiScalePercent,
            AppSettings.MaximumUiScalePercent);
    }

    private void UpdateUiScaleValueLabel(int uiScalePercent)
    {
        UiScaleValueTextBlock.Text = $"{uiScalePercent}%";
    }

    private void UpdateCursorModeSummary()
    {
        Settings.Normalize();
        CursorModeSummaryTextBlock.Text = string.Equals(Settings.CursorUsageMode, AppSettings.CursorUsageModeTeamsApiKey, StringComparison.Ordinal)
            ? "Mode: Teams Admin API key"
            : "Mode: Personal subscription dashboard login";
    }

    private void MergeSavedCursorDashboardLogin()
    {
        var savedSettings = _settingsService.Load();
        Settings.CursorDashboardCookieHeaderProtected = savedSettings.CursorDashboardCookieHeaderProtected;
        Settings.CursorDashboardCookiesCapturedAt = savedSettings.CursorDashboardCookiesCapturedAt;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RemoveMinimizeMaximizeButtons();
    }

    private void RemoveMinimizeMaximizeButtons()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlStyle);
        SetWindowLong(handle, GwlStyle, style & ~WsMaximizeBox & ~WsMinimizeBox);
    }

    private const int GwlStyle = -16;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private sealed record ProviderSetupInfo(
        string Title,
        string Message,
        string LinkText,
        string Url,
        Action<Window>? LinkAction = null);
}

public sealed class UiScalePreviewChangedEventArgs : EventArgs
{
    public UiScalePreviewChangedEventArgs(int uiScalePercent)
    {
        UiScalePercent = uiScalePercent;
    }

    public int UiScalePercent { get; }
}
