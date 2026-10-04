using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using vNoder.Core;
using vNoder.Nodes;

namespace vNoder.App.Presentation;

/// <summary>Port dot and wire colour, keyed by the port's CLR type.</summary>
public sealed class PortBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Number = Frozen("#FF6FB3FF");
    private static readonly SolidColorBrush Text = Frozen("#FFF2C14E");
    private static readonly SolidColorBrush Boolean = Frozen("#FF7BD88F");
    private static readonly SolidColorBrush Bytes = Frozen("#FFC792EA");
    private static readonly SolidColorBrush List = Frozen("#FF5FD4C4");
    private static readonly SolidColorBrush DateTimeBrush = Frozen("#FFFF8A65");
    private static readonly SolidColorBrush Any = Frozen("#FFAEB4C0");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        For(value as Type);

    /// <summary>The brush for a port type; also used directly by <see cref="WireLayer"/>.</summary>
    public static SolidColorBrush For(Type? type) => type switch
    {
        _ when type == typeof(double) => Number,
        _ when type == typeof(string) => Text,
        _ when type == typeof(bool) => Boolean,
        _ when type == typeof(byte[]) => Bytes,
        _ when type is not null && PortTypeCompatibility.IsList(type) => List,
        _ when type == typeof(DateTime) => DateTimeBrush,
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
        ["Output"] = PortBrushConverter.Frozen("#FF3C4758"),
        ["Encoding"] = PortBrushConverter.Frozen("#FF2F5F6E"),
        ["Network"] = PortBrushConverter.Frozen("#FF2E4F86"),
        ["Image"] = PortBrushConverter.Frozen("#FF7A3F8C"),
        ["Convert"] = PortBrushConverter.Frozen("#FF4A5A78"),
        ["JSON"] = PortBrushConverter.Frozen("#FF7A6A28"),
        ["List"] = PortBrushConverter.Frozen("#FF23766A"),
        ["Date"] = PortBrushConverter.Frozen("#FF94502E"),
        ["Path"] = PortBrushConverter.Frozen("#FF6E5530"),
        ["System"] = PortBrushConverter.Frozen("#FF803A3A"),
        ["Flow"] = PortBrushConverter.Frozen("#FF5E6B2A"),
        ["Data"] = PortBrushConverter.Frozen("#FF2F6A4F"),
        ["CLI"] = PortBrushConverter.Frozen("#FF3F5A3A"),
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

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        For(value is double s ? s : 1);

    /// <summary>The thickness for a zoom level; also used directly by <see cref="WireLayer"/>.</summary>
    public static double For(double scale)
    {
        scale = scale > 0.01 ? scale : 1;

        // Clamped so a deep zoom-in does not leave hairlines and a deep zoom-out does not leave ropes.
        return Math.Clamp(OnScreenThickness / scale, 1.2, 10);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The values of whatever enum the bound property holds, so an enum property renders as a combo box
/// with <c>ItemsSource="{Binding X, Converter={StaticResource EnumValues}}"</c> and nothing else: no
/// <c>ObjectDataProvider</c> resource per enum type.
/// </summary>
public sealed class EnumValuesConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Enum ? Enum.GetValues(value.GetType()) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>An image from encoded bytes (PNG, JPEG, ...), for previews; null when there is none.</summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
            return null;

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = new System.IO.MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // Not an image: show nothing rather than break the binding.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The Table node's rows as a <see cref="DataView"/>, which a DataGrid generates columns for. Columns
/// are named c0, c1 … with the real header as the caption: a DataGrid reads a column name as a binding
/// path, so a header such as "a.b" or "x[1]" would bind to nothing.
/// </summary>
public sealed class TableToViewConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TableSnapshot table)
            return null;

        var data = new DataTable();
        for (var i = 0; i < table.Columns.Count; i++)
            data.Columns.Add(new DataColumn($"c{i}", typeof(string)) { Caption = table.Columns[i] });

        foreach (var row in table.Rows)
        {
            var cells = new object[table.Columns.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = i < row.Length ? row[i] : string.Empty;

            data.Rows.Add(cells);
        }

        return data.DefaultView;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The Plot node's values as polyline points in the plot area's own pixels: oldest at the left, newest
/// at the right, the lowest value at the bottom and the highest at the top. A flat series sits
/// mid-height rather than dividing by a zero range.
/// </summary>
public sealed class PlotPointsConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var points = new PointCollection();
        if (values is not [double[] { Length: > 0 } series, double width, double height] || width <= 0 || height <= 0)
            return points;

        var min = series.Min();
        var range = series.Max() - min;
        var step = series.Length > 1 ? width / (series.Length - 1) : 0;

        for (var i = 0; i < series.Length; i++)
        {
            var y = range == 0 ? height / 2 : height - ((series[i] - min) / range * height);
            points.Add(new Point(series.Length > 1 ? i * step : width / 2, y));
        }

        points.Freeze();
        return points;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"290 × 290" for a decoded image, empty for none; captions the Image node's preview.</summary>
public sealed class ImageSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is System.Windows.Media.Imaging.BitmapSource image
            ? $"{image.PixelWidth} × {image.PixelHeight}"
            : string.Empty;

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
