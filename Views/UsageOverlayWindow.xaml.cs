using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageMonitor.Models;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Views;

public partial class UsageOverlayWindow : Window
{
    private const string FullDisplayMode = "Full";
    private const string CompactDisplayMode = "Compact";
    private const string MiniDisplayMode = "Mini";
    private const double CompactWidthBreakpoint = 760;
    private const double MiniWidthBreakpoint = 500;
    private const double MiniHeightBreakpoint = 290;
    private const double FullMinimumCardWidth = 330;
    private const double CompactMinimumCardWidth = 220;
    private const double MiniMinimumCardWidth = 136;
    private const double CardAreaTargetAspect = 1.6;
    private const double CardFullMinCellWidth = 265;
    private const double CardFullMinCellHeight = 250;
    private const double CardCompactMinCellWidth = 200;
    private const double CardCompactMinCellHeight = 90;
    private const double MaxCardScale = 1.5;
    private const double CardScaleBaseWidth = 300;
    private const double CardScaleBaseHeight = 250;
    private const double FullEmptyMinimumHeight = 240;
    private const double CompactEmptyMinimumHeight = 96;
    private const double MiniEmptyMinimumHeight = 58;
    private const double FullMinimumVerticalInset = 210;
    private const double FullMinimumRowHeight = 265;
    private const double CompactMinimumVerticalInset = 36;
    private const double CompactMinimumRowHeight = 100;
    private const double MiniMinimumVerticalInset = 22;
    private const double MiniMinimumRowHeight = 38;
    private const double MiniHorizontalChromeInset = 54;
    private const double CompactHorizontalChromeInset = 28;
    private const double CompactButtonsFallbackWidth = 175;
    private const double CompactButtonsGap = 10;
    private const double HeightTrimTolerance = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    public static readonly DependencyProperty DisplayModeProperty = DependencyProperty.Register(
        nameof(DisplayMode),
        typeof(string),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(FullDisplayMode));

    public static readonly DependencyProperty ShowCompactButtonsProperty = DependencyProperty.Register(
        nameof(ShowCompactButtons),
        typeof(bool),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(false));

    public static readonly DependencyProperty CardSlotWidthProperty = DependencyProperty.Register(
        nameof(CardSlotWidth),
        typeof(double),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(400d));

    public static readonly DependencyProperty ProvidersListWidthProperty = DependencyProperty.Register(
        nameof(ProvidersListWidth),
        typeof(double),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(816d));

    public static readonly DependencyProperty ProvidersListMarginProperty = DependencyProperty.Register(
        nameof(ProvidersListMargin),
        typeof(Thickness),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(new Thickness(0, 18, 0, 0)));

    public static readonly DependencyProperty UiScaleProperty = DependencyProperty.Register(
        nameof(UiScale),
        typeof(double),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(1d, UiScalePropertyChanged));

    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(
        nameof(CardColumns),
        typeof(int),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(1));

    public static readonly DependencyProperty CardDetailLevelProperty = DependencyProperty.Register(
        nameof(CardDetailLevel),
        typeof(string),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(FullDisplayMode));

    public static readonly DependencyProperty CardContentScaleProperty = DependencyProperty.Register(
        nameof(CardContentScale),
        typeof(double),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(1d));

    private double _resizeStartHeight;
    private double _resizeStartWidth;
    private System.Windows.Point _resizeStartScreenPoint;
    private Thumb? _activeResizeThumb;
    private INotifyCollectionChanged? _providersCollection;
    private bool _responsiveLayoutQueued;

    public event EventHandler? ReloadRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? LogsRequested;

    public event EventHandler? ExitRequested;

    public UsageOverlayWindow()
    {
        InitializeComponent();
        Loaded += WindowOnLoaded;
        IsVisibleChanged += WindowOnIsVisibleChanged;
        SizeChanged += WindowOnSizeChanged;
        StateChanged += WindowOnStateChanged;
        DataContextChanged += WindowOnDataContextChanged;
    }

    internal void EnsureTopmost()
    {
        if (!Topmost || !IsVisible)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public void ApplyStartupPlacement(OverlayWindowPlacement? placement)
    {
        var virtualScreenBounds = GetVirtualScreenBounds();
        if (virtualScreenBounds.IsEmpty)
        {
            return;
        }

        var width = CoerceDimension(placement?.Width, Width, MinWidth, virtualScreenBounds.Width);
        var height = CoerceDimension(placement?.Height, Height, MinHeight, virtualScreenBounds.Height);

        Width = width;
        Height = height;

        var savedLeft = placement?.Left;
        var savedTop = placement?.Top;
        if (!HasFiniteValue(savedLeft) || !HasFiniteValue(savedTop))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Clamp(savedLeft.GetValueOrDefault(), virtualScreenBounds.Left, virtualScreenBounds.Right - width);
        Top = Clamp(savedTop.GetValueOrDefault(), virtualScreenBounds.Top, virtualScreenBounds.Bottom - height);
    }

    public OverlayWindowPlacement GetCurrentPlacement()
    {
        return new OverlayWindowPlacement
        {
            Left = HasFiniteValue(Left) ? Left : null,
            Top = HasFiniteValue(Top) ? Top : null,
            Width = GetCurrentDimension(ActualWidth, Width),
            Height = GetCurrentDimension(ActualHeight, Height)
        };
    }

    public void EnsureValidPlacement()
    {
        ApplyStartupPlacement(GetCurrentPlacement());
    }

    public void CenterOnPrimaryScreen()
    {
        var workArea = GetPrimaryScreenWorkArea();
        if (workArea.IsEmpty)
        {
            EnsureValidPlacement();
            return;
        }

        var width = CoerceDimension(GetCurrentDimension(ActualWidth, Width), Width, MinWidth, workArea.Width);
        var height = CoerceDimension(GetCurrentDimension(ActualHeight, Height), Height, MinHeight, workArea.Height);

        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Clamp(workArea.Left + (workArea.Width - width) / 2, workArea.Left, workArea.Right - width);
        Top = Clamp(workArea.Top + (workArea.Height - height) / 2, workArea.Top, workArea.Bottom - height);
    }

    public string DisplayMode
    {
        get => (string)GetValue(DisplayModeProperty);
        private set => SetValue(DisplayModeProperty, value);
    }

    public bool ShowCompactButtons
    {
        get => (bool)GetValue(ShowCompactButtonsProperty);
        private set => SetValue(ShowCompactButtonsProperty, value);
    }

    public double CardSlotWidth
    {
        get => (double)GetValue(CardSlotWidthProperty);
        private set => SetValue(CardSlotWidthProperty, value);
    }

    public double ProvidersListWidth
    {
        get => (double)GetValue(ProvidersListWidthProperty);
        private set => SetValue(ProvidersListWidthProperty, value);
    }

    public Thickness ProvidersListMargin
    {
        get => (Thickness)GetValue(ProvidersListMarginProperty);
        private set => SetValue(ProvidersListMarginProperty, value);
    }

    public double UiScale
    {
        get => (double)GetValue(UiScaleProperty);
        private set => SetValue(UiScaleProperty, value);
    }

    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        private set => SetValue(CardColumnsProperty, value);
    }

    public string CardDetailLevel
    {
        get => (string)GetValue(CardDetailLevelProperty);
        private set => SetValue(CardDetailLevelProperty, value);
    }

    public double CardContentScale
    {
        get => (double)GetValue(CardContentScaleProperty);
        private set => SetValue(CardContentScaleProperty, value);
    }

    public void ApplyUiScalePercent(int scalePercent)
    {
        var normalizedScalePercent = NormalizeUiScalePercent(scalePercent);
        var scale = normalizedScalePercent / 100d;

        if (Math.Abs(UiScale - scale) < 0.001)
        {
            QueueResponsiveLayoutUpdate();
            return;
        }

        UiScale = scale;
    }

    public void ApplyAlwaysOnTop(bool alwaysOnTop)
    {
        Topmost = alwaysOnTop;
        if (alwaysOnTop)
        {
            EnsureTopmost();
        }
    }

    private void MinimizeButtonOnClick(object sender, RoutedEventArgs e)
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    private void HideButtonOnClick(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ShowMenuItemOnClick(object sender, RoutedEventArgs e)
    {
        Show();
        Activate();
        EnsureTopmost();
    }

    private void RefreshMenuItemOnClick(object sender, RoutedEventArgs e)
    {
        ReloadRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SettingsMenuItemOnClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LogsMenuItemOnClick(object sender, RoutedEventArgs e)
    {
        LogsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExitMenuItemOnClick(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadButtonOnClick(object sender, RoutedEventArgs e)
    {
        ReloadRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SettingsButtonOnClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LogsButtonOnClick(object sender, RoutedEventArgs e)
    {
        LogsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HeaderOnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        BeginDragMove(e);
    }

    private void WindowOnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (e.ButtonState != MouseButtonState.Pressed || IsInteractiveElement(source))
        {
            return;
        }

        BeginDragMove(e);
    }

    private void ResizeThumbOnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is Thumb thumb)
        {
            _activeResizeThumb = thumb;
            _activeResizeThumb.Visibility = Visibility.Visible;
        }

        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        _resizeStartScreenPoint = GetMouseScreenPosition();
    }

    private void ResizeThumbOnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var delta = ScreenPixelsToDips(GetMouseScreenPosition() - _resizeStartScreenPoint);
        Width = Math.Max(MinWidth, _resizeStartWidth + delta.X);
        Height = Math.Max(MinHeight, _resizeStartHeight + delta.Y);
    }

    private void ResizeThumbOnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_activeResizeThumb is null)
        {
            return;
        }

        _activeResizeThumb.ClearValue(VisibilityProperty);
        _activeResizeThumb = null;
    }

    private void ProvidersListOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueResponsiveLayoutUpdate();
    }

    private void CompactButtonsPanelOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueResponsiveLayoutUpdate();
    }

    private void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        QueueResponsiveLayoutUpdate();
        EnsureTopmost();
    }

    private void WindowOnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(new Action(EnsureTopmost), DispatcherPriority.Loaded);
        }
    }

    private void WindowOnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            ShowInTaskbar = true;
            return;
        }

        ShowInTaskbar = false;
        EnsureTopmost();
    }

    private void WindowOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueResponsiveLayoutUpdate();
    }

    private void WindowOnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_providersCollection is not null)
        {
            _providersCollection.CollectionChanged -= ProvidersOnCollectionChanged;
            _providersCollection = null;
        }

        if (e.NewValue is UsageOverlayViewModel viewModel)
        {
            _providersCollection = viewModel.Providers;
            _providersCollection.CollectionChanged += ProvidersOnCollectionChanged;
        }

        QueueResponsiveLayoutUpdate();
    }

    private void ProvidersOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueResponsiveLayoutUpdate();
    }

    private void QueueResponsiveLayoutUpdate()
    {
        if (_responsiveLayoutQueued)
        {
            return;
        }

        _responsiveLayoutQueued = true;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _responsiveLayoutQueued = false;
                UpdateResponsiveLayout();
            }),
            DispatcherPriority.Loaded);
    }

    private void UpdateResponsiveLayout()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var uiScale = EffectiveUiScale;
        var widthDip = ToLogicalDimension(ActualWidth, uiScale);
        var heightDip = ToLogicalDimension(ActualHeight, uiScale);
        var providerCount = ProvidersList.Items.Count;

        // Chrome density (header / footer / padding) still keys off the overall window size,
        // so the surrounding shell keeps matching the existing Full / Compact / Mini looks.
        var chromeMode = GetChromeMode(widthDip, heightDip);
        if (!string.Equals(DisplayMode, chromeMode, StringComparison.Ordinal))
        {
            DisplayMode = chromeMode;
        }

        var showCompactButtons = chromeMode == CompactDisplayMode;
        if (ShowCompactButtons != showCompactButtons)
        {
            ShowCompactButtons = showCompactButtons;
        }

        UpdateProvidersListMargin(chromeMode, showCompactButtons);

        // Fluid both-axis card grid: pick the column count that best fills the available area,
        // then derive each card's detail level from the resulting cell size (not the window width).
        var availableWidth = Math.Max(1, widthDip - GetHorizontalChromeInset(chromeMode, showCompactButtons));
        var availableHeight = Math.Max(1, heightDip - GetVerticalChromeInset(chromeMode));
        var columns = ChooseCardColumns(availableWidth, availableHeight, providerCount);
        if (CardColumns != columns)
        {
            CardColumns = columns;
        }

        var rows = providerCount > 0 ? (int)Math.Ceiling(providerCount / (double)columns) : 1;
        var cellWidth = columns > 0 ? availableWidth / columns : availableWidth;
        var cellHeight = rows > 0 ? availableHeight / rows : availableHeight;
        var detailLevel = GetCardDetailLevel(cellWidth, cellHeight);
        if (!string.Equals(CardDetailLevel, detailLevel, StringComparison.Ordinal))
        {
            CardDetailLevel = detailLevel;
        }

        // Grow the card content as the cell gets bigger so large tiles read like a full dashboard
        // panel. Width-limited (Min) so the scaled-up bars/text never overflow the card.
        var contentScale = Math.Clamp(
            Math.Min(cellWidth / CardScaleBaseWidth, cellHeight / CardScaleBaseHeight),
            1d,
            MaxCardScale);
        if (Math.Abs(CardContentScale - contentScale) > 0.001)
        {
            CardContentScale = contentScale;
        }

        // Small floor so the window can be dragged down to a single Mini card in any direction.
        // Height is intentionally left free (no trim-to-content) so dragging down actually grows it.
        MinWidth = ToPhysicalDimension(MiniHorizontalChromeInset + MiniMinimumCardWidth, uiScale);
        MinHeight = ToPhysicalDimension(MiniMinimumVerticalInset + MiniMinimumRowHeight, uiScale);
    }

    private void UpdateProvidersListMargin(string displayMode, bool showCompactButtons)
    {
        var margin = displayMode switch
        {
            MiniDisplayMode => new Thickness(0),
            CompactDisplayMode when showCompactButtons => new Thickness(0, 0, GetCompactButtonsRightInset(), 0),
            CompactDisplayMode => new Thickness(0),
            _ => new Thickness(0, 18, 0, 0)
        };

        if (ProvidersListMargin != margin)
        {
            ProvidersListMargin = margin;
        }
    }

    private double GetHorizontalChromeInset(string displayMode, bool showCompactButtons)
    {
        return displayMode switch
        {
            MiniDisplayMode => MiniHorizontalChromeInset,
            CompactDisplayMode => CompactHorizontalChromeInset + (showCompactButtons ? GetCompactButtonsRightInset() : 0),
            _ => 84
        };
    }

    private double GetCompactButtonsRightInset()
    {
        var buttonsWidth = CompactButtonsPanel is { ActualWidth: > 0 } panel
            ? panel.ActualWidth
            : CompactButtonsFallbackWidth;

        return Math.Ceiling(buttonsWidth + CompactButtonsGap);
    }

    private static double GetVerticalChromeInset(string displayMode)
    {
        return displayMode switch
        {
            MiniDisplayMode => MiniMinimumVerticalInset,
            CompactDisplayMode => CompactMinimumVerticalInset,
            _ => FullMinimumVerticalInset
        };
    }

    // Chrome density (big title header vs. compact buttons vs. bare strip) keys off the overall
    // window size so the shell keeps matching the existing Full / Compact / Mini screenshots.
    private static string GetChromeMode(double widthDip, double heightDip)
    {
        if (widthDip < 460 || heightDip < 110)
        {
            return MiniDisplayMode;
        }

        if (widthDip < CompactWidthBreakpoint || heightDip < 360)
        {
            return CompactDisplayMode;
        }

        return FullDisplayMode;
    }

    // Pick the column count whose resulting cells sit closest to a comfortable card aspect ratio,
    // so a wide window spreads cards across columns and a tall / narrow window stacks them into rows.
    private static int ChooseCardColumns(double availableWidth, double availableHeight, int providerCount)
    {
        if (providerCount <= 1)
        {
            return 1;
        }

        var bestColumns = 1;
        var bestScore = double.MaxValue;
        for (var columns = 1; columns <= providerCount; columns++)
        {
            var rows = (int)Math.Ceiling(providerCount / (double)columns);
            var cellWidth = availableWidth / columns;
            var cellHeight = availableHeight / rows;
            if (cellWidth <= 0 || cellHeight <= 0)
            {
                continue;
            }

            var aspect = cellWidth / cellHeight;
            var score = Math.Abs(Math.Log(aspect) - Math.Log(CardAreaTargetAspect)) + 0.08 * (columns * rows - providerCount);
            if (score < bestScore)
            {
                bestScore = score;
                bestColumns = columns;
            }
        }

        return bestColumns;
    }

    private static string GetCardDetailLevel(double cellWidth, double cellHeight)
    {
        if (cellWidth >= CardFullMinCellWidth && cellHeight >= CardFullMinCellHeight)
        {
            return FullDisplayMode;
        }

        if (cellWidth >= CardCompactMinCellWidth && cellHeight >= CardCompactMinCellHeight)
        {
            return CompactDisplayMode;
        }

        return MiniDisplayMode;
    }

    private static double GetMinimumWindowHeight(string displayMode, int rows)
    {
        if (rows <= 0)
        {
            return displayMode switch
            {
                MiniDisplayMode => MiniEmptyMinimumHeight,
                CompactDisplayMode => CompactEmptyMinimumHeight,
                _ => FullEmptyMinimumHeight
            };
        }

        return displayMode switch
        {
            MiniDisplayMode => MiniMinimumVerticalInset + rows * MiniMinimumRowHeight,
            CompactDisplayMode => CompactMinimumVerticalInset + rows * CompactMinimumRowHeight,
            _ => FullMinimumVerticalInset + rows * FullMinimumRowHeight
        };
    }

    private static int ChooseColumnsForLayout(int providerCount, int maxColumns)
    {
        var bestColumns = 1;
        var bestRows = int.MaxValue;

        for (var columns = 1; columns <= maxColumns; columns++)
        {
            var rows = (int)Math.Ceiling(providerCount / (double)columns);

            if (rows < bestRows)
            {
                bestColumns = columns;
                bestRows = rows;
            }
        }

        return bestColumns;
    }

    private static string GetDisplayMode(double width, double height, int providerCount)
    {
        if (width < MiniWidthBreakpoint ||
            (height < MiniHeightBreakpoint && !CanFitMode(CompactDisplayMode, width, height, providerCount, false)))
        {
            return MiniDisplayMode;
        }

        if (width >= CompactWidthBreakpoint &&
            CanFitMode(FullDisplayMode, width, height, providerCount, false))
        {
            return FullDisplayMode;
        }

        return CanFitMode(CompactDisplayMode, width, height, providerCount, false)
            ? CompactDisplayMode
            : MiniDisplayMode;
    }

    private static bool CanFitMode(string displayMode, double width, double height, int providerCount, bool showCompactButtons)
    {
        var grid = CalculateCardGrid(displayMode, width, height, providerCount, showCompactButtons);
        return GetMinimumWindowHeight(displayMode, grid.Rows) <= height;
    }

    private static CardGrid CalculateCardGrid(
        string displayMode,
        double width,
        double height,
        int providerCount,
        bool showCompactButtons)
    {
        var availableWidth = Math.Max(1, width - EstimatedHorizontalChromeInset(displayMode, showCompactButtons));
        if (providerCount <= 0)
        {
            return new CardGrid(0, 0, availableWidth, availableWidth);
        }

        var minimumCardWidth = displayMode switch
        {
            MiniDisplayMode => MiniMinimumCardWidth,
            CompactDisplayMode => CompactMinimumCardWidth,
            _ => FullMinimumCardWidth
        };
        var maxColumns = Math.Clamp((int)Math.Floor(availableWidth / minimumCardWidth), 1, providerCount);
        var columns = ChooseColumnsForLayout(providerCount, maxColumns);
        var rows = (int)Math.Ceiling(providerCount / (double)columns);
        var cardSlotWidth = Math.Max(1, Math.Floor(availableWidth / columns));

        return new CardGrid(columns, rows, cardSlotWidth, cardSlotWidth * columns);
    }

    private static double EstimatedHorizontalChromeInset(string displayMode, bool showCompactButtons)
    {
        return displayMode switch
        {
            MiniDisplayMode => MiniHorizontalChromeInset,
            CompactDisplayMode => CompactHorizontalChromeInset + (showCompactButtons ? CompactButtonsFallbackWidth + CompactButtonsGap : 0),
            _ => 84
        };
    }

    private double EffectiveUiScale => CoerceUiScale(UiScale);

    private static void UiScalePropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is UsageOverlayWindow window)
        {
            window.QueueResponsiveLayoutUpdate();
        }
    }

    private static double CoerceUiScale(double scale)
    {
        return HasFiniteValue(scale) && scale > 0
            ? scale
            : 1d;
    }

    private static double ToLogicalDimension(double physicalDimension, double uiScale)
    {
        return physicalDimension / CoerceUiScale(uiScale);
    }

    private static double ToPhysicalDimension(double logicalDimension, double uiScale)
    {
        return Math.Ceiling(logicalDimension * CoerceUiScale(uiScale));
    }

    private static int NormalizeUiScalePercent(int uiScalePercent)
    {
        if (uiScalePercent <= 0)
        {
            return AppSettings.DefaultUiScalePercent;
        }

        var clampedScale = Math.Clamp(
            uiScalePercent,
            AppSettings.MinimumUiScalePercent,
            AppSettings.MaximumUiScalePercent);
        var stepCount = (int)Math.Round(
            (clampedScale - AppSettings.MinimumUiScalePercent) / (double)AppSettings.UiScaleStepPercent,
            MidpointRounding.AwayFromZero);
        var steppedScale = AppSettings.MinimumUiScalePercent + stepCount * AppSettings.UiScaleStepPercent;

        return Math.Clamp(
            steppedScale,
            AppSettings.MinimumUiScalePercent,
            AppSettings.MaximumUiScalePercent);
    }

    private static Rect GetVirtualScreenBounds()
    {
        if (!HasFiniteValue(SystemParameters.VirtualScreenLeft) ||
            !HasFiniteValue(SystemParameters.VirtualScreenTop) ||
            !HasFiniteValue(SystemParameters.VirtualScreenWidth) ||
            !HasFiniteValue(SystemParameters.VirtualScreenHeight) ||
            SystemParameters.VirtualScreenWidth <= 0 ||
            SystemParameters.VirtualScreenHeight <= 0)
        {
            return Rect.Empty;
        }

        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    private static Rect GetPrimaryScreenWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        if (!HasFiniteValue(workArea.Left) ||
            !HasFiniteValue(workArea.Top) ||
            !HasFiniteValue(workArea.Width) ||
            !HasFiniteValue(workArea.Height) ||
            workArea.Width <= 0 ||
            workArea.Height <= 0)
        {
            return Rect.Empty;
        }

        return workArea;
    }

    private static double CoerceDimension(double? savedValue, double fallbackValue, double minimumValue, double maximumValue)
    {
        var effectiveMinimum = HasFiniteValue(minimumValue) && minimumValue > 0
            ? minimumValue
            : 1;
        var effectiveMaximum = HasFiniteValue(maximumValue) && maximumValue > 0
            ? Math.Max(effectiveMinimum, maximumValue)
            : effectiveMinimum;
        var value = fallbackValue;
        if (HasFiniteValue(savedValue) && savedValue.GetValueOrDefault() > 0)
        {
            value = savedValue.GetValueOrDefault();
        }

        if (!HasFiniteValue(value) || value <= 0)
        {
            value = effectiveMinimum;
        }

        return Clamp(value, effectiveMinimum, effectiveMaximum);
    }

    private static double? GetCurrentDimension(double actualValue, double configuredValue)
    {
        var value = HasFiniteValue(actualValue) && actualValue > 0
            ? actualValue
            : configuredValue;

        return HasFiniteValue(value) && value > 0
            ? value
            : null;
    }

    private static double Clamp(double value, double minimumValue, double maximumValue)
    {
        if (!HasFiniteValue(value))
        {
            return minimumValue;
        }

        if (maximumValue < minimumValue)
        {
            return minimumValue;
        }

        return Math.Min(Math.Max(value, minimumValue), maximumValue);
    }

    private static bool HasFiniteValue(double? value)
    {
        return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
    }

    private readonly record struct CardGrid(
        int Columns,
        int Rows,
        double CardSlotWidth,
        double ProvidersListWidth);

    private System.Windows.Point GetMouseScreenPosition()
    {
        return PointToScreen(Mouse.GetPosition(this));
    }

    private Vector ScreenPixelsToDips(Vector screenPixels)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.Transform(screenPixels) ?? screenPixels;
    }

    private void BeginDragMove(MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse capture changes during the drag.
        }
        finally
        {
            EnsureTopmost();
        }
    }

    private void WindowOnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_providersCollection is not null)
        {
            _providersCollection.CollectionChanged -= ProvidersOnCollectionChanged;
            _providersCollection = null;
        }

        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnsureTopmost();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or Thumb)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
