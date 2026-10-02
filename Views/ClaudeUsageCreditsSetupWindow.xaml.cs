using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using Microsoft.Web.WebView2.Core;

namespace AIUsageMonitor.Views;

public partial class ClaudeUsageCreditsSetupWindow : Window
{
    private readonly ProviderAccount _account;
    private readonly ClaudeBillingService _billing;
    private readonly AppLogService _log;
    private readonly string _profileId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private bool _saving;
    private bool _saved;
    private bool _closeRequested;
    private bool _closed;

    public ClaudeUsageCreditsSetupWindow(ProviderAccount account, AppSettingsService settings, AppLogService log)
    {
        InitializeComponent();
        _account = account.Clone();
        _billing = new ClaudeBillingService(settings);
        _log = log;
        AccountText.Text = $"Usage credits for {account.CardLabel}: {account.Email}";
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        StatusText.Text = "Loading Claude sign-in...";
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _billing.GetProfileDirectory(_profileId));
            if (_closed) return;
            await BillingWebView.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            BillingWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            BillingWebView.CoreWebView2.Navigate("https://claude.ai/settings/billing");
            ConnectButton.IsEnabled = true;
            StatusText.Text = "Sign into the account above, then click Connect.";
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (!_closed) StatusText.Text = "Could not open Claude sign-in. Check that the Microsoft Edge WebView2 runtime is installed.";
        }
        finally
        {
            if (_closed)
            {
                try { await Task.Run(() => _billing.DeleteProfileAsync(_profileId)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _log.Warning(KnownProviders.Anthropic, "Cancelled Claude billing browser profile could not be removed yet."); }
            }
        }
    }

    private async void ConnectOnClick(object sender, RoutedEventArgs e)
    {
        if (_saving || BillingWebView.CoreWebView2 is null) return;
        if (!Uri.TryCreate(BillingWebView.Source?.AbsoluteUri, UriKind.Absolute, out var source) ||
            source.Scheme != "https" || source.Host != "claude.ai")
        {
            StatusText.Text = "Finish signing in to Claude before connecting.";
            return;
        }
        _saving = true;
        ConnectButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Checking that this login matches the account and reading its balance...";
            var cookies = await BillingWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai/api/account");
            if (!cookies.Any(cookie => cookie.Name == "sessionKey" && !string.IsNullOrWhiteSpace(cookie.Value)))
            {
                StatusText.Text = "No Claude login was found. Finish signing in, then click Connect again.";
                return;
            }
            var cookieHeader = string.Join("; ", cookies.GroupBy(cookie => cookie.Name).Select(group => $"{group.Key}={group.First().Value}"));
            var userAgent = BillingWebView.CoreWebView2.Settings.UserAgent;
            await using var session = ClaudeBillingBrowser.FromVisibleBrowser(BillingWebView.CoreWebView2, Dispatcher);
            await Task.Run(() => _billing.ConnectAsync(_account, cookieHeader, _profileId, _lifetime.Token, userAgent, session));
            _saved = true;
            _saving = false;
            DialogResult = true;
        }
        catch (ClaudeBillingException ex) { StatusText.Text = ex.Message; }
        catch (OperationCanceledException)
        {
            StatusText.Text = _lifetime.IsCancellationRequested ? "Connection cancelled." : "Claude took too long to respond. Click Connect to try again.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or
            CryptographicException or InvalidOperationException or KeyNotFoundException or System.Runtime.InteropServices.COMException)
        {
            // Do not log raw HTTP or cookie exceptions; they may contain authentication details.
            StatusText.Text = "Could not verify the billing connection. Check this account's Claude login and try again.";
        }
        finally
        {
            _saving = false;
            if (!_closed)
            {
                ConnectButton.IsEnabled = true;
                if (_closeRequested) Close();
            }
        }
    }

    private void CloseOnClick(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saving)
        {
            _closeRequested = true;
            _lifetime.Cancel();
            e.Cancel = true;
            StatusText.Text = "Cancelling connection...";
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        BillingWebView.Dispose();
        var unusedProfile = _saved ? _account.ClaudeBilling?.Id : _profileId;
        if (!string.IsNullOrWhiteSpace(unusedProfile))
        {
            try { await Task.Run(() => _billing.DeleteProfileAsync(unusedProfile)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _log.Warning(KnownProviders.Anthropic, "Unused Claude billing browser profile could not be removed yet."); }
        }
    }
}
