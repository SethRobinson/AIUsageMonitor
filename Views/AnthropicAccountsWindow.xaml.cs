using System.IO;
using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfGrid = System.Windows.Controls.Grid;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace AIUsageMonitor.Views;

public partial class AnthropicAccountsWindow : Window
{
    private static readonly WpfBrush PrimaryTextBrush = new SolidColorBrush(WpfColor.FromRgb(0xD6, 0xDA, 0xE1));
    private static readonly WpfBrush SecondaryTextBrush = new SolidColorBrush(WpfColor.FromRgb(0x8B, 0x93, 0xA1));
    private static readonly WpfBrush ActiveBadgeBrush = new SolidColorBrush(WpfColor.FromRgb(0x6E, 0xE7, 0xA0));
    private static readonly WpfBrush RowBackgroundBrush = new SolidColorBrush(WpfColor.FromRgb(0x1A, 0x1D, 0x25));
    private static readonly WpfBrush RowBorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x30, 0x36, 0x41));
    private static readonly WpfBrush InputBackgroundBrush = new SolidColorBrush(WpfColor.FromRgb(0x1D, 0x20, 0x28));
    private static readonly WpfBrush InputForegroundBrush = new SolidColorBrush(WpfColor.FromRgb(0xF9, 0xFA, 0xFB));

    private readonly AppSettingsService _settingsService;
    private readonly AnthropicAccountManagerService _accountManager;
    private readonly ClaudeAccountSwitchService _switchService;
    private readonly ClaudeSlotIdentityService? _slotIdentityService;

    private CancellationTokenSource? _loginCancellation;
    private string _activeAccountUuid = string.Empty;
    private bool _busy;

    public AnthropicAccountsWindow(AppSettingsService settingsService, AppLogService logService, bool screenshotMode = false)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _accountManager = new AnthropicAccountManagerService(settingsService, logService);
        _switchService = new ClaudeAccountSwitchService(settingsService, logService, _accountManager);
        _slotIdentityService = screenshotMode
            ? null
            : new ClaudeSlotIdentityService(logService, _accountManager);

        if (screenshotMode)
        {
            // Render purely from the provided (fake) settings: no ~/.claude reads, no
            // network identity fetch, no self-repair.
            var accounts = settingsService.Load().GetAccounts(KnownProviders.Anthropic);
            var defaultAccount = accounts.FirstOrDefault(account => account.IsDefault);
            _activeAccountUuid = defaultAccount?.AccountUuid ?? string.Empty;
            var activeAccount = accounts.FirstOrDefault(account => !account.IsDefault &&
                string.Equals(account.AccountUuid, _activeAccountUuid, StringComparison.OrdinalIgnoreCase)) ?? defaultAccount;
            UpdateActiveAccountText(activeAccount?.Email ?? string.Empty, _activeAccountUuid);
            RebuildAccountRows();
            return;
        }

        // Seed from what the CLI itself cached so the rows are right immediately, then
        // confirm over the network; the stored uuid alone can be a login old.
        _activeAccountUuid = _slotIdentityService?.GetIdentity()?.Uuid ?? string.Empty;
        RebuildAccountRows();
        _ = DetectActiveAccountAsync();
    }

    // True when the overlay should refresh because accounts changed or the CLI account was
    // switched, even if the parent Settings dialog is later cancelled.
    public bool RefreshRequested { get; private set; }

    private void CloseButtonOnClick(object sender, RoutedEventArgs e)
    {
        _loginCancellation?.Cancel();
        DialogResult = true;
    }

    private async void AddAccountButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var label = PromptForLabel();
        if (label is null)
        {
            return;
        }

        var account = _accountManager.CreateAccount(label);
        RefreshRequested = true;
        RebuildAccountRows();
        await RunLoginAsync(account);
    }

    private async Task RunLoginAsync(ProviderAccount account)
    {
        _busy = true;
        _loginCancellation = new CancellationTokenSource();
        AddAccountButton.IsEnabled = false;
        CancelLoginButton.Visibility = Visibility.Visible;
        StatusTextBlock.Text = $"Waiting for claude /login to finish in the terminal window for '{account.Label}'... " +
            "If the login screen did not open automatically, type /login in that window.";

        try
        {
            var result = await _accountManager.LaunchLoginAsync(account, _loginCancellation.Token);
            if (result.Succeeded)
            {
                var loggedInText = string.IsNullOrWhiteSpace(result.Message)
                    ? $"'{account.Label}' is logged in."
                    : $"'{account.Label}' is logged in as {result.Message}.";
                var isNowActive = !string.IsNullOrWhiteSpace(account.AccountUuid) &&
                    string.Equals(account.AccountUuid, _activeAccountUuid, StringComparison.OrdinalIgnoreCase);
                StatusTextBlock.Text = isNowActive
                    ? loggedInText
                    : loggedInText + " Its usage is now monitored; the claude CLI itself still uses the active account above. Click 'Switch CLI to this' to change that.";
                RefreshRequested = true;
            }
            else
            {
                StatusTextBlock.Text = result.Message;
            }
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Login wait cancelled. Use 'Log in again' on the account to retry detection.";
        }
        finally
        {
            _busy = false;
            _loginCancellation?.Dispose();
            _loginCancellation = null;
            AddAccountButton.IsEnabled = true;
            CancelLoginButton.Visibility = Visibility.Collapsed;
            RebuildAccountRows();
            _ = DetectActiveAccountAsync();
        }
    }

    private void CancelLoginButtonOnClick(object sender, RoutedEventArgs e)
    {
        _loginCancellation?.Cancel();
    }

    private string? PromptForLabel()
    {
        var promptWindow = new Window
        {
            Title = "Add Claude account",
            Width = 380,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Background,
            Foreground = Foreground,
            Owner = this,
            ShowInTaskbar = false
        };

        var panel = new WpfStackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new WpfTextBlock
        {
            Text = "Name this account (for example: Work, Personal, Max):",
            Foreground = PrimaryTextBrush,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        var labelTextBox = new WpfTextBox
        {
            Margin = new Thickness(0, 10, 0, 0),
            Height = 32,
            Padding = new Thickness(8, 5, 8, 5),
            Background = InputBackgroundBrush,
            Foreground = InputForegroundBrush,
            CaretBrush = InputForegroundBrush,
            BorderBrush = RowBorderBrush,
            FontSize = 14
        };
        panel.Children.Add(labelTextBox);

        var buttonPanel = new WpfStackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var okButton = new WpfButton { Content = "Start login", Width = 100, Height = 30, IsDefault = true };
        var cancelButton = new WpfButton { Content = "Cancel", Width = 80, Height = 30, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        okButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(labelTextBox.Text))
            {
                promptWindow.DialogResult = true;
            }
        };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        panel.Children.Add(buttonPanel);

        promptWindow.Content = panel;
        labelTextBox.Focus();

        return promptWindow.ShowDialog() == true ? labelTextBox.Text.Trim() : null;
    }

    private async Task DetectActiveAccountAsync()
    {
        if (_slotIdentityService is null || !_slotIdentityService.HasLogin)
        {
            ActiveAccountTextBlock.Text = "Active claude CLI account: none (no ~/.claude login found).";
            return;
        }

        var identity = await _slotIdentityService.ResolveAsync(CancellationToken.None);
        if (identity is null || string.IsNullOrWhiteSpace(identity.Uuid))
        {
            ActiveAccountTextBlock.Text = "Active claude CLI account: could not be checked right now.";
            return;
        }

        _activeAccountUuid = identity.Uuid;

        // Keep the default entry's identity current: it drives the active badge and lets
        // the aggregator hide the duplicate card of whichever account is active in ~/.claude.
        var settings = _settingsService.Load();
        var defaultAccount = settings.ProviderAccounts.FirstOrDefault(account => account.IsDefault &&
            string.Equals(account.ProviderName, KnownProviders.Anthropic, StringComparison.OrdinalIgnoreCase));
        if (defaultAccount is not null &&
            (!string.Equals(defaultAccount.AccountUuid, identity.Uuid, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(defaultAccount.Email, identity.Email, StringComparison.OrdinalIgnoreCase)))
        {
            defaultAccount.AccountUuid = identity.Uuid;
            defaultAccount.Email = identity.Email;
            _settingsService.Save(settings);
            RefreshRequested = true;
        }

        UpdateActiveAccountText(identity.Email, identity.Uuid);

        // Self-heal a split-brain (token belongs to one account while claude /status shows
        // another) as soon as it is detected; no user action needed.
        var activeManagedAccount = _settingsService.Load().GetAccounts(KnownProviders.Anthropic).FirstOrDefault(account =>
            !account.IsDefault &&
            string.Equals(account.AccountUuid, identity.Uuid, StringComparison.OrdinalIgnoreCase));
        if (activeManagedAccount is not null &&
            await _switchService.RepairIdentityCacheAsync(activeManagedAccount, CancellationToken.None))
        {
            StatusTextBlock.Text = $"Repaired stale account info: claude /status was still showing a previous account " +
                $"while '{activeManagedAccount.Label}' is the one actually in use. Restart open claude sessions to see the correct name.";
        }

        RebuildAccountRows();
    }

    private void UpdateActiveAccountText(string email, string uuid)
    {
        var matchingAccount = _settingsService.Load().GetAccounts(KnownProviders.Anthropic).FirstOrDefault(account =>
            !account.IsDefault &&
            string.Equals(account.AccountUuid, uuid, StringComparison.OrdinalIgnoreCase));
        var display = string.IsNullOrWhiteSpace(email) ? "unknown" : email;
        ActiveAccountTextBlock.Text = matchingAccount is null
            ? $"Active claude CLI account: {display}"
            : $"Active claude CLI account: {display} ({matchingAccount.Label})";
    }

    private void RebuildAccountRows()
    {
        AccountsPanel.Children.Clear();

        var settings = _settingsService.Load();
        var accounts = settings.GetAccounts(KnownProviders.Anthropic);
        // Live detection wins over the stored value: the CLI can be re-logged in from
        // anywhere, and a stale uuid here badges the wrong row as active.
        var slotUuid = string.IsNullOrWhiteSpace(_activeAccountUuid)
            ? accounts.FirstOrDefault(account => account.IsDefault)?.AccountUuid
            : _activeAccountUuid;

        var haveActiveIdentity = !string.IsNullOrWhiteSpace(slotUuid);
        var managedAccountIsActive = haveActiveIdentity && accounts.Any(account =>
            !account.IsDefault &&
            string.Equals(account.AccountUuid, slotUuid, StringComparison.OrdinalIgnoreCase));

        foreach (var account in accounts)
        {
            // Once the CLI is logged into an account that has its own entry, the Default
            // row is redundant (it IS that account) and is hidden.
            if (account.IsDefault && managedAccountIsActive)
            {
                continue;
            }

            // Exactly one row gets the active badge: the managed account matching the
            // ~/.claude identity, or the default row when no managed account matches.
            var isActive = account.IsDefault
                ? haveActiveIdentity
                : haveActiveIdentity && string.Equals(account.AccountUuid, slotUuid, StringComparison.OrdinalIgnoreCase);
            AccountsPanel.Children.Add(BuildAccountRow(account, isActive));
        }
    }

    private System.Windows.Controls.Border BuildAccountRow(ProviderAccount account, bool isActive)
    {
        var grid = new WpfGrid();
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

        var infoPanel = new WpfStackPanel();

        var headerPanel = new WpfStackPanel { Orientation = WpfOrientation.Horizontal };
        var enabledCheckBox = new WpfCheckBox
        {
            IsChecked = account.Enabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        enabledCheckBox.Checked += (_, _) => SetEnabled(account, true);
        enabledCheckBox.Unchecked += (_, _) => SetEnabled(account, false);
        headerPanel.Children.Add(enabledCheckBox);

        var labelTextBox = new WpfTextBox
        {
            Text = account.Label,
            Width = 170,
            Height = 26,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 2, 6, 2),
            Background = InputBackgroundBrush,
            Foreground = InputForegroundBrush,
            CaretBrush = InputForegroundBrush,
            BorderBrush = RowBorderBrush,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        labelTextBox.LostFocus += (_, _) => RenameAccount(account, labelTextBox.Text);
        headerPanel.Children.Add(labelTextBox);

        if (isActive)
        {
            headerPanel.Children.Add(new WpfTextBlock
            {
                Text = "● active in ~/.claude",
                Foreground = ActiveBadgeBrush,
                FontSize = 11,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        infoPanel.Children.Add(headerPanel);

        var detail = account.IsDefault
            ? string.IsNullOrWhiteSpace(account.Email)
                ? "Whatever the claude CLI (~/.claude) is currently logged into."
                : $"Whatever the claude CLI (~/.claude) is currently logged into. Now: {account.Email}"
            : string.IsNullOrWhiteSpace(account.Email)
                ? "Not logged in yet."
                : account.Email;
        infoPanel.Children.Add(new WpfTextBlock
        {
            Text = detail,
            Foreground = SecondaryTextBrush,
            FontSize = 12,
            Margin = new Thickness(24, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        grid.Children.Add(infoPanel);

        var buttonPanel = new WpfStackPanel { Margin = new Thickness(12, 0, 0, 0) };
        if (!account.IsDefault)
        {
            var switchButton = new WpfButton
            {
                Content = isActive ? "Active in CLI" : "Switch CLI to this",
                Width = 150,
                Height = 28,
                IsEnabled = !isActive && !_busy
            };
            switchButton.Click += async (_, _) => await SwitchToAccountAsync(account);
            buttonPanel.Children.Add(switchButton);

            var loginButton = new WpfButton
            {
                Content = "Log in again",
                Width = 150,
                Height = 28,
                Margin = new Thickness(0, 6, 0, 0),
                IsEnabled = !_busy
            };
            loginButton.Click += async (_, _) =>
            {
                if (!_busy)
                {
                    await RunLoginAsync(account);
                }
            };
            buttonPanel.Children.Add(loginButton);

            var removeButton = new WpfButton
            {
                Content = "Remove",
                Width = 150,
                Height = 28,
                Margin = new Thickness(0, 6, 0, 0),
                IsEnabled = !_busy
            };
            removeButton.Click += (_, _) => RemoveAccount(account);
            buttonPanel.Children.Add(removeButton);
        }

        System.Windows.Controls.Grid.SetColumn(buttonPanel, 1);
        grid.Children.Add(buttonPanel);

        return new System.Windows.Controls.Border
        {
            Background = RowBackgroundBrush,
            BorderBrush = RowBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private void SetEnabled(ProviderAccount account, bool enabled)
    {
        _accountManager.SetAccountEnabled(account.Id, enabled);
        RefreshRequested = true;
    }

    private void RenameAccount(ProviderAccount account, string newLabel)
    {
        if (string.IsNullOrWhiteSpace(newLabel) ||
            string.Equals(newLabel.Trim(), account.Label, StringComparison.Ordinal))
        {
            return;
        }

        if (_accountManager.RenameAccount(account.Id, newLabel))
        {
            account.Label = newLabel.Trim();
            RefreshRequested = true;
        }
    }

    private void RemoveAccount(ProviderAccount account)
    {
        var confirmation = WpfMessageBox.Show(
            this,
            $"Remove the account '{account.Label}'? Its saved login folder will be deleted. This does not touch ~/.claude or the account itself.",
            "Remove Claude account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (_accountManager.RemoveAccount(account.Id))
        {
            RefreshRequested = true;
            StatusTextBlock.Text = $"Removed '{account.Label}'.";
            RebuildAccountRows();
        }
    }

    private async Task SwitchToAccountAsync(ProviderAccount account)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        StatusTextBlock.Text = $"Switching the Claude CLI account to '{account.Label}'...";
        RebuildAccountRows();

        try
        {
            var result = await _switchService.SwitchToAsync(account, CancellationToken.None);
            StatusTextBlock.Text = result.Message;
            if (result.Succeeded && !result.AlreadyActive)
            {
                RefreshRequested = true;
                _activeAccountUuid = account.AccountUuid;
                UpdateActiveAccountText(account.Email, account.AccountUuid);
            }
        }
        finally
        {
            _busy = false;
            RebuildAccountRows();
        }
    }
}
