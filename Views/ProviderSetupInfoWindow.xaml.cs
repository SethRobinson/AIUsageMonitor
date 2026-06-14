using System.Diagnostics;
using System.Windows;

namespace AIUsageMonitor.Views;

public partial class ProviderSetupInfoWindow : Window
{
    private readonly string _setupUrl;
    private readonly Action<Window>? _setupLinkAction;

    public ProviderSetupInfoWindow(
        string title,
        string message,
        string linkText,
        string setupUrl,
        Action<Window>? setupLinkAction = null)
    {
        InitializeComponent();
        _setupUrl = setupUrl;
        _setupLinkAction = setupLinkAction;
        Title = title;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        SetupLinkRun.Text = linkText;
    }

    private void SetupHyperlinkOnClick(object sender, RoutedEventArgs e)
    {
        if (_setupLinkAction is not null)
        {
            _setupLinkAction(this);
            return;
        }

        OpenSetupPage();
    }

    private void OpenSetupPageButtonOnClick(object sender, RoutedEventArgs e)
    {
        OpenSetupPage();
    }

    private void CloseButtonOnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenSetupPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _setupUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Could not open setup page: {ex.Message}",
                "Setup page",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
