using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Views;

public partial class UsageOverlayWindow : Window
{
    private const string FullDisplayMode = "Full";
    private const string CompactDisplayMode = "Compact";
    private const string MiniDisplayMode = "Mini";
    private const double CompactButtonsFallbackWidth = 175;
    private const double CompactButtonsGap = 10;
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

    public static readonly DependencyProperty CardSlotHeightProperty = DependencyProperty.Register(
        nameof(CardSlotHeight),
        typeof(double),
        typeof(UsageOverlayWindow),
        new PropertyMetadata(250d));

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
    private double _resizeStartLeft;
    private double _resizeStartTop;
    private double _resizeStartWidth;
    private System.Windows.Point _resizeStartScreenPoint;
    private Thumb? _activeResizeThumb;
    private ResizeDirection _activeResizeDirection;
    private INotifyCollectionChanged? _providersCollection;
    private bool _responsiveLayoutQueued;
    private ResponsiveLayoutPlan? _responsiveLayoutPlan;

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

    public double CardSlotHeight
    {
        get => (double)GetValue(CardSlotHeightProperty);
        private set => SetValue(CardSlotHeightProperty, value);
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

    internal ResponsiveLayoutPlan? CurrentResponsiveLayout => _responsiveLayoutPlan;

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
        if (sender is not Thumb thumb ||
            !Enum.TryParse(thumb.Tag as string, out ResizeDirection direction) ||
            direction == ResizeDirection.None)
        {
            return;
        }

        _activeResizeThumb = thumb;
        _activeResizeThumb.Visibility = Visibility.Visible;
        _activeResizeDirection = direction;
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        _resizeStartLeft = Left;
        _resizeStartTop = Top;
        _resizeStartScreenPoint = GetMouseScreenPosition();
    }

    private void ResizeThumbOnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_activeResizeDirection == ResizeDirection.None)
        {
            return;
        }

        var delta = ScreenPixelsToDips(GetMouseScreenPosition() - _resizeStartScreenPoint);
        var width = _resizeStartWidth;
        var height = _resizeStartHeight;
        var left = _resizeStartLeft;
        var top = _resizeStartTop;

        if (_activeResizeDirection.HasFlag(ResizeDirection.Left))
        {
            width = Math.Max(MinWidth, _resizeStartWidth - delta.X);
            left = _resizeStartLeft + _resizeStartWidth - width;
        }
        else if (_activeResizeDirection.HasFlag(ResizeDirection.Right))
        {
            width = Math.Max(MinWidth, _resizeStartWidth + delta.X);
        }

        if (_activeResizeDirection.HasFlag(ResizeDirection.Top))
        {
            height = Math.Max(MinHeight, _resizeStartHeight - delta.Y);
            top = _resizeStartTop + _resizeStartHeight - height;
        }
        else if (_activeResizeDirection.HasFlag(ResizeDirection.Bottom))
        {
            height = Math.Max(MinHeight, _resizeStartHeight + delta.Y);
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private void ResizeThumbOnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_activeResizeThumb is null)
        {
            return;
        }

        _activeResizeThumb.ClearValue(VisibilityProperty);
        _activeResizeThumb = null;
        _activeResizeDirection = ResizeDirection.None;
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
        var profiles = ProvidersList.Items
            .OfType<ProviderUsageCard>()
            .Select(card => new CardContentProfile(
                card.Windows.Count,
                !string.IsNullOrWhiteSpace(card.StatusMessage),
                card.IsUnavailable))
            .ToList();
        var plan = ResponsiveLayoutEngine.Calculate(new ResponsiveLayoutInput(
            ActualWidth,
            ActualHeight,
            uiScale,
            profiles,
            GetCompactButtonsRightInset(),
            _responsiveLayoutPlan));
        _responsiveLayoutPlan = plan;

        var chromeMode = plan.ChromeMode.ToString();
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
        var columns = Math.Max(1, plan.Columns);
        if (CardColumns != columns)
        {
            CardColumns = columns;
        }

        if (Math.Abs(CardSlotHeight - plan.SlotHeight) > 0.001)
        {
            CardSlotHeight = plan.SlotHeight;
        }

        if (Math.Abs(CardSlotWidth - plan.MinimumSlotWidth) > 0.001)
        {
            CardSlotWidth = plan.MinimumSlotWidth;
        }

        var detailLevel = plan.DetailLevel.ToString();
        if (!string.Equals(CardDetailLevel, detailLevel, StringComparison.Ordinal))
        {
            CardDetailLevel = detailLevel;
        }

        if (Math.Abs(CardContentScale - plan.ContentScale) > 0.001)
        {
            CardContentScale = plan.ContentScale;
        }

        MinWidth = ToPhysicalDimension(
            ResponsiveLayoutEngine.MiniHorizontalChromeInset + ResponsiveLayoutEngine.MinimumMiniCardWidth,
            uiScale);
        MinHeight = ToPhysicalDimension(
            ResponsiveLayoutEngine.MiniVerticalChromeInset + ResponsiveLayoutEngine.MinimumMiniRowHeight,
            uiScale);
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

    private double GetCompactButtonsRightInset()
    {
        var buttonsWidth = CompactButtonsPanel is { ActualWidth: > 0 } panel
            ? panel.ActualWidth
            : CompactButtonsFallbackWidth;

        return Math.Ceiling(buttonsWidth + CompactButtonsGap);
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

    [Flags]
    private enum ResizeDirection
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
        TopLeft = Top | Left,
        TopRight = Top | Right,
        BottomLeft = Bottom | Left,
        BottomRight = Bottom | Right
    }

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
