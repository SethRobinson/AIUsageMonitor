using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIUsageMonitor.Models;
using AIUsageMonitor.ViewModels;
using AIUsageMonitor.Views;

namespace AIUsageMonitor.Services;

internal static class ScreenshotService
{
    public static void SaveOverlayScreenshot(string outputPath, double? requestedWidth = null, double? requestedHeight = null)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var viewModel = new UsageOverlayViewModel();
        var snapshot = RebaseSnapshot(new UsageDataService().Load(), DateTimeOffset.Now);
        viewModel.ApplySnapshot(snapshot, "Sample data");
        ApplyCheckingState(viewModel, snapshot);

        var window = new UsageOverlayWindow
        {
            DataContext = viewModel,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false,
            Topmost = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        if (requestedWidth is > 0)
        {
            window.Width = requestedWidth.Value;
        }

        if (requestedHeight is > 0)
        {
            window.Height = requestedHeight.Value;
        }

        window.ApplyUiScalePercent(AppSettings.DefaultUiScalePercent);

        try
        {
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            SaveBitmap(bitmap, fullPath, outputDirectory);
        }
        finally
        {
            window.Close();
        }
    }

    public static void SaveSettingsScreenshot(string outputPath, double? requestedWidth = null, double? requestedHeight = null)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // Render the dialog from DEFAULT settings (fake), never the user's real monitor.settings.json.
        var window = new SettingsWindow(new AppSettings(), new AppSettingsService(), new AppLogService())
        {
            Left = 0,
            Top = 0,
            ShowInTaskbar = false,
            Topmost = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = requestedWidth is > 0 ? requestedWidth.Value : 540
        };

        if (requestedHeight is > 0)
        {
            window.Height = requestedHeight.Value;
        }
        else
        {
            // Grow to fit every option so the dialog reads as fully expanded with no scrollbar.
            window.SizeToContent = SizeToContent.Height;
        }

        // Show the default (unchecked) auto-run state rather than this machine's real registry setting.
        window.AutoRunAtLoginCheckBox.IsChecked = false;

        try
        {
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            SaveBitmap(bitmap, fullPath, outputDirectory);
        }
        finally
        {
            window.Close();
        }
    }

    private static void SaveBitmap(BitmapSource bitmap, string fullPath, string? outputDirectory)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var tempPath = Path.Combine(
            outputDirectory ?? Environment.CurrentDirectory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = File.Create(tempPath))
            {
                encoder.Save(stream);
            }

            File.Move(tempPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static UsageSnapshot RebaseSnapshot(UsageSnapshot snapshot, DateTimeOffset generatedAt)
    {
        var originalGeneratedAt = snapshot.GeneratedAt == default ? generatedAt : snapshot.GeneratedAt;

        return new UsageSnapshot
        {
            GeneratedAt = generatedAt,
            Source = snapshot.Source,
            Providers = snapshot.Providers
                .Select(provider => new ProviderUsage
                {
                    Name = provider.Name,
                    PlanName = provider.PlanName,
                    Source = provider.Source,
                    StatusMessage = provider.StatusMessage,
                    IsUnavailable = provider.IsUnavailable,
                    LastCheckedAt = RebaseTime(provider.LastCheckedAt, originalGeneratedAt, generatedAt),
                    Windows = provider.Windows
                        .Select(window => new UsageWindow
                        {
                            Title = window.Title,
                            Limit = window.Limit,
                            Used = window.Used,
                            Remaining = window.Remaining,
                            ResetAt = RebaseTime(window.ResetAt, originalGeneratedAt, generatedAt),
                            Detail = window.Detail
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static DateTimeOffset? RebaseTime(
        DateTimeOffset? value,
        DateTimeOffset originalGeneratedAt,
        DateTimeOffset generatedAt)
    {
        return value is { } timestamp
            ? generatedAt + (timestamp - originalGeneratedAt)
            : null;
    }

    private static void ApplyCheckingState(UsageOverlayViewModel viewModel, UsageSnapshot snapshot)
    {
        foreach (var provider in snapshot.Providers.Where(IsCheckingProvider))
        {
            var card = viewModel.Providers.FirstOrDefault(candidate =>
                string.Equals(candidate.ShortName, provider.Name, StringComparison.OrdinalIgnoreCase));
            card?.SetChecking(true);
        }
    }

    private static bool IsCheckingProvider(ProviderUsage provider)
    {
        return provider.IsUnavailable &&
               provider.StatusMessage.Contains("checking", StringComparison.OrdinalIgnoreCase);
    }
}
