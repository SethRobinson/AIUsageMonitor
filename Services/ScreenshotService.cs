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
        var snapshot = new UsageDataService().Load();
        viewModel.ApplySnapshot(snapshot, "Sample data");

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
        finally
        {
            window.Close();
        }
    }
}
