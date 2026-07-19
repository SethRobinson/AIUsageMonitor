using System.Windows;
using System.Windows.Controls;
using WpfPanel = System.Windows.Controls.Panel;
using WpfSize = System.Windows.Size;

namespace AIUsageMonitor.Views;

/// <summary>
/// A UniformGrid-like panel whose final row expands to consume the full width instead of reserving
/// invisible cells. Source order is preserved and every row remains evenly divided.
/// </summary>
public sealed class BalancedCardPanel : WpfPanel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(BalancedCardPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, Math.Max(1, value));
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
        {
            return new WpfSize();
        }

        var columns = Math.Clamp(Columns, 1, count);
        var rows = (int)Math.Ceiling(count / (double)columns);
        var finiteWidth = double.IsFinite(availableSize.Width);
        var finiteHeight = double.IsFinite(availableSize.Height);
        var measureWidth = finiteWidth ? Math.Max(0, availableSize.Width / columns) : double.PositiveInfinity;
        var measureHeight = finiteHeight ? Math.Max(0, availableSize.Height / rows) : double.PositiveInfinity;
        var childConstraint = new WpfSize(measureWidth, measureHeight);
        var maximumDesiredWidth = 0d;
        var maximumDesiredHeight = 0d;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(childConstraint);
            maximumDesiredWidth = Math.Max(maximumDesiredWidth, child.DesiredSize.Width);
            maximumDesiredHeight = Math.Max(maximumDesiredHeight, child.DesiredSize.Height);
        }

        return new WpfSize(
            finiteWidth ? availableSize.Width : maximumDesiredWidth * columns,
            finiteHeight ? availableSize.Height : maximumDesiredHeight * rows);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
        {
            return finalSize;
        }

        var columns = Math.Clamp(Columns, 1, count);
        var rows = (int)Math.Ceiling(count / (double)columns);
        var rowHeight = finalSize.Height / rows;
        var childIndex = 0;

        for (var row = 0; row < rows; row++)
        {
            var itemsInRow = Math.Min(columns, count - childIndex);
            var cellWidth = finalSize.Width / itemsInRow;
            for (var column = 0; column < itemsInRow; column++)
            {
                InternalChildren[childIndex++].Arrange(new Rect(
                    column * cellWidth,
                    row * rowHeight,
                    cellWidth,
                    rowHeight));
            }
        }

        return finalSize;
    }
}
