using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIUsageMonitor.Models;
using AIUsageMonitor.ViewModels;
using AIUsageMonitor.Views;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace AIUsageMonitor.Services;

internal sealed record LayoutSweepSummary(string OutputDirectory, int CaseCount, int HardFailureCount, int WarningCount);

internal static class LayoutSweepService
{
    private const int ThumbnailWidth = 240;
    private const int ThumbnailHeight = 120;
    private const int ContactSheetColumns = 6;
    private const int ContactSheetRows = 5;
    private const int CasesPerContactSheet = ContactSheetColumns * ContactSheetRows;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly int[] CoreWidths = [320, 360, 400, 459, 460, 461, 500, 600, 759, 760, 761, 900, 1100, 1280, 1440, 1469, 1593, 1679, 1920, 2560];
    private static readonly int[] CoreHeights = [56, 60, 90, 97, 109, 110, 111, 136, 180, 210, 240, 289, 290, 359, 360, 361, 409, 740, 1080, 1440];
    private static readonly (int Width, int Height)[] TargetSizes =
    [
        (320, 56),
        (460, 110),
        (600, 240),
        (760, 360),
        (900, 600),
        (1440, 136),
        (1440, 740),
        (1593, 409)
    ];

    public static LayoutSweepSummary Run(string outputDirectory)
    {
        var runDirectory = Path.Combine(
            Path.GetFullPath(outputDirectory),
            $"run-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runDirectory);

        var scenarios = CreateScenarios();
        var cases = BuildCases(scenarios);
        var renders = new List<SweepRender>(cases.Count);
        var initialScenario = scenarios["five-normal"];
        var window = new UsageOverlayWindow
        {
            DataContext = initialScenario,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false,
            Topmost = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        try
        {
            window.Show();
            FlushLayout(window);

            foreach (var sweepCase in cases)
            {
                window.DataContext = scenarios[sweepCase.Scenario];
                window.ApplyUiScalePercent(sweepCase.UiScalePercent);
                window.Width = sweepCase.Width;
                window.Height = sweepCase.Height;
                FlushLayout(window);

                var result = MeasureCase(window, sweepCase);
                var bitmap = RenderWindow(window);
                renders.Add(new SweepRender(sweepCase, result, CreateThumbnail(bitmap)));
            }

            var hardFailures = renders.Count(render => render.Result.HardFailureCount > 0);
            var warnings = renders.Count(render => render.Result.WarningCount > 0);
            var report = new LayoutSweepReport(
                DateTimeOffset.Now,
                renders.Count,
                hardFailures,
                warnings,
                renders.Select(render => render.Result).ToList());
            File.WriteAllText(
                Path.Combine(runDirectory, "report.json"),
                JsonSerializer.Serialize(report, JsonOptions));

            SaveContactSheets(renders, runDirectory, "contact");
            var boundaryRenders = renders
                .Where(render => IsBoundaryCase(render.Case))
                .Take(CasesPerContactSheet * 2)
                .ToList();
            SaveContactSheets(boundaryRenders, runDirectory, "boundaries");
            SaveWorstCases(window, scenarios, renders, runDirectory);

            Console.WriteLine($"Layout sweep: {renders.Count} cases, {hardFailures} hard failures, {warnings} warnings.");
            Console.WriteLine($"Layout sweep output: {runDirectory}");
            return new LayoutSweepSummary(runDirectory, renders.Count, hardFailures, warnings);
        }
        finally
        {
            window.Close();
        }
    }

    private static IReadOnlyDictionary<string, UsageOverlayViewModel> CreateScenarios()
    {
        return new Dictionary<string, UsageOverlayViewModel>(StringComparer.OrdinalIgnoreCase)
        {
            ["empty"] = CreateViewModel([]),
            ["single-unavailable"] = CreateViewModel(
            [
                CreateProvider("Unavailable Provider With A Deliberately Long Name", 0, unavailable: true)
            ]),
            ["three-mixed"] = CreateMixedViewModel(),
            ["five-normal"] = CreateViewModel(
            [
                CreateProvider("Anthropic - Corp", 2),
                CreateProvider("Anthropic - Corp Fable", 1),
                CreateProvider("Anthropic - Seth", 2),
                CreateProvider("Anthropic - Seth Fable", 1),
                CreateProvider("OpenAI", 1)
            ]),
            ["seven-dense"] = CreateViewModel(
            [
                CreateProvider("Anthropic", 4),
                CreateProvider("Anthropic - Work", 2),
                CreateProvider("Anthropic API Credits", 1),
                CreateProvider("OpenAI", 2),
                CreateProvider("Antigravity", 3),
                CreateProvider("Gemini", 2),
                CreateProvider("Cursor", 1)
            ]),
            ["twelve-long"] = CreateViewModel(Enumerable.Range(1, 12)
                .Select(index => CreateProvider($"Provider Account {index:00} With A Long Descriptive Label", 1 + index % 3))
                .ToArray())
        };
    }

    private static UsageOverlayViewModel CreateMixedViewModel()
    {
        var viewModel = CreateViewModel(
        [
            CreateProvider("Normal Provider", 2),
            CreateProvider("Unavailable Provider", 0, unavailable: true),
            CreateProvider("Checking Provider", 1)
        ]);
        viewModel.Providers[2].SetChecking(true);
        return viewModel;
    }

    private static UsageOverlayViewModel CreateViewModel(IReadOnlyList<ProviderUsage> providers)
    {
        var viewModel = new UsageOverlayViewModel();
        viewModel.ApplySnapshot(new UsageSnapshot
        {
            GeneratedAt = DateTimeOffset.Now,
            Source = "Layout sweep fake data",
            Providers = providers.ToList()
        });
        return viewModel;
    }

    private static ProviderUsage CreateProvider(string name, int windowCount, bool unavailable = false)
    {
        return new ProviderUsage
        {
            Name = name,
            PlanName = unavailable ? string.Empty : "Demo",
            Source = "Layout sweep fake data",
            IsUnavailable = unavailable,
            StatusMessage = unavailable ? "Synthetic provider unavailable for layout testing." : string.Empty,
            LastCheckedAt = DateTimeOffset.Now,
            Windows = Enumerable.Range(1, windowCount)
                .Select(index => new UsageWindow
                {
                    Title = index == 1 ? "5h" : index == 2 ? "7d" : $"Window {index}",
                    Limit = 100,
                    Used = 17 + index * 11,
                    Remaining = 83 - index * 11,
                    ResetAt = DateTimeOffset.Now.AddHours(index * 3)
                })
                .ToList()
        };
    }

    private static List<LayoutSweepCase> BuildCases(IReadOnlyDictionary<string, UsageOverlayViewModel> scenarios)
    {
        var result = new List<LayoutSweepCase>(444);
        foreach (var width in CoreWidths)
        {
            foreach (var height in CoreHeights)
            {
                result.Add(new LayoutSweepCase($"core-{width}x{height}", "five-normal", width, height, 100));
            }
        }

        var targetedScenarios = new[] { "empty", "single-unavailable", "three-mixed", "seven-dense", "twelve-long" };
        foreach (var scenario in targetedScenarios)
        {
            if (!scenarios.ContainsKey(scenario))
            {
                continue;
            }

            foreach (var scale in new[] { 80, 100, 150 })
            {
                foreach (var (width, height) in TargetSizes)
                {
                    result.Add(new LayoutSweepCase(
                        $"target-{scenario}-{scale}-{width}x{height}",
                        scenario,
                        width,
                        height,
                        scale));
                }
            }
        }

        return result;
    }

    private static void FlushLayout(UsageOverlayWindow window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static LayoutSweepCaseResult MeasureCase(UsageOverlayWindow window, LayoutSweepCase sweepCase)
    {
        var plan = window.CurrentResponsiveLayout;
        var hardIssues = new List<string>();
        var warnings = new List<string>();
        if (plan is null)
        {
            hardIssues.Add("No responsive layout plan was produced.");
            return LayoutSweepCaseResult.MissingPlan(sweepCase, window.ActualWidth, window.ActualHeight, hardIssues);
        }

        var panel = FindVisualChild<BalancedCardPanel>(window.ProvidersList);
        var rowCoverage = 1d;
        var contentSpans = new List<double>();
        var largestEmptyGapRatios = new List<double>();
        var clippingCount = 0;
        var overlapCount = 0;
        var outOfBoundsCount = 0;
        var missingContentCount = 0;

        if (panel is not null && panel.Children.Count > 0)
        {
            var childBounds = panel.Children
                .OfType<UIElement>()
                .Select(child => GetBoundsRelativeTo(child, panel))
                .ToList();
            outOfBoundsCount = childBounds.Count(bounds =>
                bounds.Left < -0.5 || bounds.Top < -0.5 ||
                bounds.Right > panel.ActualWidth + 0.5 || bounds.Bottom > panel.ActualHeight + 0.5);
            for (var left = 0; left < childBounds.Count; left++)
            {
                for (var right = left + 1; right < childBounds.Count; right++)
                {
                    var intersection = WpfRect.Intersect(childBounds[left], childBounds[right]);
                    if (!intersection.IsEmpty && intersection.Width > 0.5 && intersection.Height > 0.5)
                    {
                        overlapCount++;
                    }
                }
            }

            var groupedRows = childBounds
                .GroupBy(bounds => Math.Round(bounds.Top, 1))
                .ToList();
            rowCoverage = groupedRows.Min(row =>
            {
                var left = row.Min(bounds => bounds.Left);
                var right = row.Max(bounds => bounds.Right);
                return (right - left) / Math.Max(1, panel.ActualWidth);
            });

            foreach (UIElement child in panel.Children)
            {
                var contentBounds = EnumerateVisualDescendants(child)
                    .Where(element => element is TextBlock or WpfProgressBar)
                    .Where(element => element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0)
                    .Select(element => GetBoundsRelativeTo(element, child))
                    .Where(bounds => !bounds.IsEmpty)
                    .ToList();
                if (contentBounds.Count > 0 && child.RenderSize.Height > 0)
                {
                    var span = (contentBounds.Max(bounds => bounds.Bottom) - contentBounds.Min(bounds => bounds.Top)) /
                               child.RenderSize.Height;
                    contentSpans.Add(Math.Clamp(span, 0, 1));
                    largestEmptyGapRatios.Add(CalculateLargestEmptyVerticalGap(contentBounds, child.RenderSize.Height));
                }
                else
                {
                    missingContentCount++;
                }

                clippingCount += EnumerateVisualDescendants(child)
                    .OfType<TextBlock>()
                    .Count(IsCriticallyClipped);
            }
        }

        if (overlapCount > 0)
        {
            hardIssues.Add($"{overlapCount} card overlaps detected.");
        }

        if (outOfBoundsCount > 0)
        {
            hardIssues.Add($"{outOfBoundsCount} cards extend outside the provider panel.");
        }

        if (rowCoverage < 0.95)
        {
            hardIssues.Add($"Minimum row coverage was {rowCoverage:P0}, below 95%.");
        }

        if (missingContentCount > 0)
        {
            hardIssues.Add($"{missingContentCount} provider cards rendered without visible foreground content.");
        }

        var medianContentSpan = Median(contentSpans);
        var medianLargestEmptyGap = Median(largestEmptyGapRatios);
        if (plan.DetailLevel is ProviderCardDetailLevel.Compact or ProviderCardDetailLevel.MiniTall &&
            plan.SlotHeight >= 120 &&
            sweepCase.Scenario is not "empty" and not "single-unavailable" &&
            medianLargestEmptyGap > 0.45)
        {
            hardIssues.Add($"Median largest empty vertical gap was {medianLargestEmptyGap:P0}, above 45%.");
        }

        if (plan.DetailLevel != ProviderCardDetailLevel.Mini &&
            plan.Rows > 0 &&
            sweepCase.Scenario is not "empty" and not "single-unavailable" &&
            medianContentSpan < 0.5)
        {
            warnings.Add($"Median foreground content span was only {medianContentSpan:P0}.");
        }

        if (clippingCount > 0)
        {
            warnings.Add($"{clippingCount} non-trimming text elements may be clipped.");
        }

        return new LayoutSweepCaseResult(
            sweepCase.Id,
            sweepCase.Scenario,
            sweepCase.Width,
            sweepCase.Height,
            window.ActualWidth,
            window.ActualHeight,
            sweepCase.UiScalePercent,
            plan.ChromeMode.ToString(),
            plan.DetailLevel.ToString(),
            plan.Columns,
            plan.Rows,
            plan.RowItemCounts,
            plan.MinimumSlotWidth,
            plan.MaximumSlotWidth,
            plan.SlotHeight,
            plan.ContentScale,
            rowCoverage,
            medianContentSpan,
            medianLargestEmptyGap,
            clippingCount,
            overlapCount,
            outOfBoundsCount,
            missingContentCount,
            hardIssues.Count,
            warnings.Count,
            hardIssues,
            warnings);
    }

    private static bool IsCriticallyClipped(TextBlock textBlock)
    {
        if (string.IsNullOrEmpty(textBlock.Text) ||
            textBlock.TextTrimming != TextTrimming.None ||
            textBlock.TextWrapping != TextWrapping.NoWrap ||
            textBlock.ActualWidth <= 0)
        {
            return false;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(textBlock).PixelsPerDip;
        var formatted = new FormattedText(
            textBlock.Text,
            CultureInfo.CurrentUICulture,
            textBlock.FlowDirection,
            new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch),
            textBlock.FontSize,
            textBlock.Foreground,
            pixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace > textBlock.ActualWidth + 1;
    }

    private static RenderTargetBitmap RenderWindow(UsageOverlayWindow window)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateThumbnail(BitmapSource bitmap)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(WpfBrushes.Black, null, new WpfRect(0, 0, ThumbnailWidth, ThumbnailHeight));
            var scale = Math.Min(ThumbnailWidth / (double)bitmap.PixelWidth, ThumbnailHeight / (double)bitmap.PixelHeight);
            var width = bitmap.PixelWidth * scale;
            var height = bitmap.PixelHeight * scale;
            drawing.DrawImage(bitmap, new WpfRect((ThumbnailWidth - width) / 2, (ThumbnailHeight - height) / 2, width, height));
        }

        var thumbnail = new RenderTargetBitmap(ThumbnailWidth, ThumbnailHeight, 96, 96, PixelFormats.Pbgra32);
        thumbnail.Render(visual);
        thumbnail.Freeze();
        return thumbnail;
    }

    private static void SaveContactSheets(IReadOnlyList<SweepRender> renders, string directory, string prefix)
    {
        for (var offset = 0; offset < renders.Count; offset += CasesPerContactSheet)
        {
            var page = renders.Skip(offset).Take(CasesPerContactSheet).ToList();
            var visual = new DrawingVisual();
            const int cellWidth = ThumbnailWidth + 20;
            const int cellHeight = ThumbnailHeight + 38;
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(
                    new SolidColorBrush(WpfColor.FromRgb(15, 17, 22)),
                    null,
                    new WpfRect(0, 0, cellWidth * ContactSheetColumns, cellHeight * ContactSheetRows));
                for (var index = 0; index < page.Count; index++)
                {
                    var column = index % ContactSheetColumns;
                    var row = index / ContactSheetColumns;
                    var x = column * cellWidth + 10;
                    var y = row * cellHeight + 6;
                    drawing.DrawImage(page[index].Thumbnail, new WpfRect(x, y, ThumbnailWidth, ThumbnailHeight));
                    var result = page[index].Result;
                    var label = $"{result.RequestedWidth}x{result.RequestedHeight} {result.UiScalePercent}% {result.Columns}x{result.Rows} {result.DetailLevel}";
                    var text = new FormattedText(
                        label,
                        CultureInfo.InvariantCulture,
                        WpfFlowDirection.LeftToRight,
                        new Typeface("Segoe UI"),
                        10,
                        result.HardFailureCount > 0 ? WpfBrushes.OrangeRed : result.WarningCount > 0 ? WpfBrushes.Gold : WpfBrushes.Gainsboro,
                        1);
                    drawing.DrawText(text, new WpfPoint(x, y + ThumbnailHeight + 5));
                }
            }

            var bitmap = new RenderTargetBitmap(
                cellWidth * ContactSheetColumns,
                cellHeight * ContactSheetRows,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            SaveBitmap(bitmap, Path.Combine(directory, $"{prefix}-{offset / CasesPerContactSheet + 1:00}.png"));
        }
    }

    private static void SaveWorstCases(
        UsageOverlayWindow window,
        IReadOnlyDictionary<string, UsageOverlayViewModel> scenarios,
        IReadOnlyList<SweepRender> renders,
        string runDirectory)
    {
        var worstDirectory = Path.Combine(runDirectory, "worst");
        Directory.CreateDirectory(worstDirectory);
        var keyCaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "core-1593x409",
            "core-1469x210",
            "core-1679x97",
            "core-1440x740",
            "core-1440x136",
            "core-1440x60"
        };
        var worst = renders
            .OrderByDescending(render => render.Result.HardFailureCount > 0)
            .ThenByDescending(render => render.Result.WarningCount > 0)
            .ThenByDescending(render => keyCaseIds.Contains(render.Case.Id))
            .ThenBy(render => render.Case.Scenario == "empty")
            .ThenBy(render => render.Result.MedianContentVerticalSpan)
            .Take(12)
            .ToList();

        foreach (var render in worst)
        {
            window.DataContext = scenarios[render.Case.Scenario];
            window.ApplyUiScalePercent(render.Case.UiScalePercent);
            window.Width = render.Case.Width;
            window.Height = render.Case.Height;
            FlushLayout(window);
            var safeName = string.Concat(render.Case.Id.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            SaveBitmap(RenderWindow(window), Path.Combine(worstDirectory, $"{safeName}.png"));
        }
    }

    private static bool IsBoundaryCase(LayoutSweepCase sweepCase)
    {
        return sweepCase.Width is 459 or 460 or 461 or 759 or 760 or 761 ||
               sweepCase.Height is 109 or 110 or 111 or 359 or 360 or 361;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<FrameworkElement> EnumerateVisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element)
            {
                yield return element;
            }

            foreach (var descendant in EnumerateVisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static WpfRect GetBoundsRelativeTo(UIElement element, Visual ancestor)
    {
        try
        {
            return element.TransformToAncestor(ancestor).TransformBounds(new WpfRect(new WpfPoint(), element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return WpfRect.Empty;
        }
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private static double CalculateLargestEmptyVerticalGap(IReadOnlyList<WpfRect> bounds, double totalHeight)
    {
        if (bounds.Count == 0 || totalHeight <= 0)
        {
            return 1;
        }

        var intervals = bounds
            .Select(bound => (Top: Math.Clamp(bound.Top, 0, totalHeight), Bottom: Math.Clamp(bound.Bottom, 0, totalHeight)))
            .Where(interval => interval.Bottom > interval.Top)
            .OrderBy(interval => interval.Top)
            .ToList();
        if (intervals.Count == 0)
        {
            return 1;
        }

        var largestGap = intervals[0].Top;
        var currentBottom = intervals[0].Bottom;
        foreach (var interval in intervals.Skip(1))
        {
            largestGap = Math.Max(largestGap, interval.Top - currentBottom);
            currentBottom = Math.Max(currentBottom, interval.Bottom);
        }

        largestGap = Math.Max(largestGap, totalHeight - currentBottom);
        return Math.Clamp(largestGap / totalHeight, 0, 1);
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed record SweepRender(LayoutSweepCase Case, LayoutSweepCaseResult Result, BitmapSource Thumbnail);

    private sealed record LayoutSweepReport(
        DateTimeOffset GeneratedAt,
        int CaseCount,
        int HardFailureCount,
        int WarningCount,
        IReadOnlyList<LayoutSweepCaseResult> Cases);

    private sealed record LayoutSweepCase(string Id, string Scenario, int Width, int Height, int UiScalePercent);

    private sealed record LayoutSweepCaseResult(
        string Id,
        string Scenario,
        int RequestedWidth,
        int RequestedHeight,
        double ActualWidth,
        double ActualHeight,
        int UiScalePercent,
        string ChromeMode,
        string DetailLevel,
        int Columns,
        int Rows,
        IReadOnlyList<int> RowItemCounts,
        double MinimumSlotWidth,
        double MaximumSlotWidth,
        double SlotHeight,
        double ContentScale,
        double MinimumRowCoverage,
        double MedianContentVerticalSpan,
        double MedianLargestEmptyVerticalGap,
        int CriticalClippingCount,
        int OverlapCount,
        int OutOfBoundsCount,
        int MissingContentCount,
        int HardFailureCount,
        int WarningCount,
        IReadOnlyList<string> HardIssues,
        IReadOnlyList<string> Warnings)
    {
        public static LayoutSweepCaseResult MissingPlan(
            LayoutSweepCase sweepCase,
            double actualWidth,
            double actualHeight,
            IReadOnlyList<string> hardIssues) =>
            new(
                sweepCase.Id,
                sweepCase.Scenario,
                sweepCase.Width,
                sweepCase.Height,
                actualWidth,
                actualHeight,
                sweepCase.UiScalePercent,
                string.Empty,
                string.Empty,
                0,
                0,
                [],
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                hardIssues.Count,
                0,
                hardIssues,
                []);
    }
}
