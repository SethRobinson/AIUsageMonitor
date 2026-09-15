using System.Diagnostics;
using System.IO;
using System.Windows;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using Wpf = System.Windows.Controls;
using Media = System.Windows.Media;

namespace AIUsageMonitor.Views;

public partial class OpenAiAccountsWindow : Window
{
    private readonly AppSettingsService _settings;
    private readonly OpenAiAccountManagerService _manager;
    private CancellationTokenSource? _login;
    private bool _closed;
    public bool RefreshRequested { get; private set; }

    public OpenAiAccountsWindow(AppSettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _manager = new OpenAiAccountManagerService(settings);
        RebuildRows();
    }

    private void RebuildRows()
    {
        var settings = _settings.Load();
        ModeText.Text = settings.OpenAiUseSavedAccounts
            ? "Monitoring saved accounts. Enable the accounts you want to see; edit a label to rename its card."
            : "Currently monitoring the automatic Codex login. Your first saved account replaces that card. Add each account you want to track.";
        AccountsPanel.Children.Clear();
        var accounts = settings.GetAccounts(KnownProviders.OpenAI);
        if (accounts.Count == 0)
            AccountsPanel.Children.Add(new Wpf.TextBlock { Text = "No saved OpenAI accounts yet.", Foreground = Brush("#A8AFBA"), Margin = new Thickness(0, 10, 0, 10) });
        foreach (var account in accounts)
        {
            var grid = new Wpf.Grid();
            grid.ColumnDefinitions.Add(new Wpf.ColumnDefinition());
            grid.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = GridLength.Auto });
            var info = new Wpf.StackPanel();
            var header = new Wpf.Grid();
            header.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new Wpf.ColumnDefinition());
            var enabled = new Wpf.CheckBox { IsChecked = account.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Show this account" };
            enabled.Click += (_, _) => TryChange(() => _manager.SetEnabled(account.Id, enabled.IsChecked == true));
            header.Children.Add(enabled);
            var label = new Wpf.TextBox
            {
                Text = account.Label, Height = 28, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 3, 6, 3),
                Background = Brush("#20242D"), Foreground = Brush("#F9FAFB"), CaretBrush = Brush("#F9FAFB"), BorderBrush = Brush("#303641")
            };
            label.LostFocus += (_, _) =>
            {
                if (label.Text.Trim() == account.Label) return;
                TryChange(() => _manager.Rename(account.Id, label.Text));
                account.Label = _settings.Load().GetAccounts(KnownProviders.OpenAI).FirstOrDefault(item => item.Id == account.Id)?.Label ?? account.Label;
                label.Text = account.Label;
            };
            Wpf.Grid.SetColumn(label, 1);
            header.Children.Add(label);
            info.Children.Add(header);
            info.Children.Add(new Wpf.TextBlock
            {
                Text = string.IsNullOrWhiteSpace(account.Email) ? "Saved ChatGPT login" : account.Email,
                Foreground = Brush("#A8AFBA"), FontSize = 12, Margin = new Thickness(24, 5, 0, 0), TextWrapping = TextWrapping.Wrap
            });
            grid.Children.Add(info);
            var actions = new Wpf.StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            var login = new Wpf.Button { Content = "Log in again", Width = 104, Height = 28 };
            login.Click += async (_, _) => await LoginAsync(account.Label, account.Id);
            var remove = new Wpf.Button { Content = "Remove", Width = 104, Height = 28, Margin = new Thickness(0, 6, 0, 0) };
            remove.Click += async (_, _) => await RemoveAsync(account);
            actions.Children.Add(login);
            actions.Children.Add(remove);
            Wpf.Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            AccountsPanel.Children.Add(new Wpf.Border
            {
                Child = grid, Background = Brush("#1A1D25"), BorderBrush = Brush("#303641"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10)
            });
        }
    }

    private async void AddOnClick(object sender, RoutedEventArgs e) => await LoginAsync(NewLabel.Text, null);

    private async Task LoginAsync(string label, string? id)
    {
        if (_login is not null) return;
        if (string.IsNullOrWhiteSpace(label)) { StatusText.Text = "Enter a label such as Personal or Work."; NewLabel.Focus(); return; }
        using var cancellation = new CancellationTokenSource();
        _login = cancellation;
        SetBusy(true);
        StatusText.Text = "Opening OpenAI sign-in. Choose the account you want to save in your browser.";
        try
        {
            var account = await Task.Run(() => _manager.LoginAsync(label, id,
                uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), cancellation.Token));
            RefreshRequested = true;
            if (!_closed)
            {
                StatusText.Text = $"Saved {account.Label}. Its usage is now monitored independently.";
                if (id is null) NewLabel.Clear();
            }
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "Login cancelled. Existing saved logins were kept."; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { if (!_closed) StatusText.Text = ex is InvalidOperationException ? ex.Message : "Could not save the login. Check the Codex installation and account folder, then retry."; }
        finally
        {
            _login = null;
            if (!_closed) { RebuildRows(); SetBusy(false); }
        }
    }

    private async Task RemoveAsync(ProviderAccount account)
    {
        if (System.Windows.MessageBox.Show(this, $"Remove '{account.Label}' and its saved monitor login?", "Remove OpenAI account",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try { await Task.Run(() => _manager.RemoveAsync(account.Id)); RefreshRequested = true; StatusText.Text = "Saved account removed."; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { StatusText.Text = ex is InvalidOperationException ? ex.Message : "Could not remove the saved account folder. Please retry."; }
        finally { if (!_closed) { RebuildRows(); SetBusy(false); } }
    }

    private void TryChange(Action action)
    {
        try { action(); RefreshRequested = true; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { StatusText.Text = ex is InvalidOperationException ? ex.Message : "Could not save account changes. Please retry."; }
    }

    private void SetBusy(bool busy)
    {
        AccountsPanel.IsEnabled = !busy;
        AddButton.IsEnabled = NewLabel.IsEnabled = !busy;
        CancelLoginButton.Visibility = busy && _login is not null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CancelLoginOnClick(object sender, RoutedEventArgs e) => _login?.Cancel();
    private void CloseOnClick(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosed(EventArgs e) { _closed = true; _login?.Cancel(); base.OnClosed(e); }
    private static Media.Brush Brush(string color) => (Media.Brush)new Media.BrushConverter().ConvertFromString(color)!;
}
