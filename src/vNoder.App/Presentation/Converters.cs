using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using vNoder.Core;

namespace vNoder.App.Presentation;

/// <summary>Port dot and wire colour, keyed by the port's CLR type.</summary>
public sealed class PortBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Number = Frozen("#FF6FB3FF");
    private static readonly SolidColorBrush Text = Frozen("#FFF2C14E");
    private static readonly SolidColorBrush Boolean = Frozen("#FF7BD88F");
    private static readonly SolidColorBrush Bytes = Frozen("#FFC792EA");
    private static readonly SolidColorBrush List = Frozen("#FF5FD4C4");
    private static readonly SolidColorBrush Any = Frozen("#FFAEB4C0");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Type t when t == typeof(double) => Number,
        Type t when t == typeof(string) => Text,
        Type t when t == typeof(bool) => Boolean,
        Type t when t == typeof(byte[]) => Bytes,
        Type t when t == typeof(string[]) => List,
        _ => Any,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Node header colour, keyed by the category half of its definition path.</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> ByCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Input"] = PortBrushConverter.Frozen("#FF35608F"),
        ["Generators"] = PortBrushConverter.Frozen("#FF6A4791"),
        ["Math"] = PortBrushConverter.Frozen("#FF2C6455"),
        ["Logic"] = PortBrushConverter.Frozen("#FF4B6430"),
        ["Crypto"] = PortBrushConverter.Frozen("#FF7C4460"),
        ["IO"] = PortBrushConverter.Frozen("#FF7D5F2B"),
        ["Control"] = PortBrushConverter.Frozen("#FF954A36"),
        ["Output"] = PortBrushConverter.Frozen("#FF3C4758"),
    };

    private static readonly SolidColorBrush Fallback = PortBrushConverter.Frozen("#FF414250");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string category && ByCategory.TryGetValue(category, out var brush) ? brush : Fallback;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// False when the named input port is connected, so an inline editor greys out once a wire is
/// driving it. Bound to the node's <c>Inputs</c> collection with the port name as the converter
/// parameter, because a node model has no per-field "is this wired" property and adding one per
/// field to every node would be exactly the boilerplate the one-class rule exists to avoid.
/// </summary>
public sealed class PortNotWiredConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<InputPort> inputs || parameter is not string name)
            return true;

        return !inputs.Any(p => p.Name == name && p.IsConnected);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Wire thickness in graph units that renders at a constant width on screen. The scene carries the
/// zoom, so a fixed thickness of 2 becomes half a pixel at 25% zoom and antialiases away to nothing,
/// which reads as wires simply not being drawn.
/// </summary>
public sealed class ScaleToStrokeThicknessConverter : IValueConverter
{
    private const double OnScreenThickness = 2;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var scale = value is double s and > 0.01 ? s : 1;

        // Clamped so a deep zoom-in does not leave hairlines and a deep zoom-out does not leave ropes.
        return Math.Clamp(OnScreenThickness / scale, 1.2, 10);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Drives the output preview's wrap toggle.</summary>
public sealed class BoolToTextWrappingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when false; the mirror of <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Tile rectangle for the canvas dot grid, derived from the current zoom and pan so the backdrop
/// tracks the content without being transformed itself.
/// </summary>
public sealed class GridViewportConverter : IMultiValueConverter
{
    private const double GraphStep = 24;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values.Any(v => v is not double))
            return new Rect(0, 0, GraphStep, GraphStep);

        var scale = (double)values[0];
        var offsetX = (double)values[1];
        var offsetY = (double)values[2];

        // Below a few pixels the dots turn into noise, so stop shrinking the tile.
        var step = Math.Max(GraphStep * scale, 8);
        return new Rect(offsetX % step, offsetY % step, step, step);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
