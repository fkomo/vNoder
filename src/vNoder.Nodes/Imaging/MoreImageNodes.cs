using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using vNoder.Core;

namespace vNoder.Nodes;

internal static class ImageColors
{
    /// <summary>A colour by name (red, white) or hex (#RGB, #RRGGBB, #RRGGBBAA).</summary>
    public static Color Parse(string text)
    {
        text = text.Trim();
        if (Color.TryParse(text, out var color) || Color.TryParseHex(text, out color))
            return color;

        throw new InvalidOperationException($"'{text}' is not a colour; use a name like white or a hex value like #FF8800.");
    }
}

/// <summary>
/// Brightness, contrast and saturation as factors, where 1 leaves the image as it is: 1.2 is 20 %
/// more, 0.8 is 20 % less, and saturation 0 is grey. Hue turns every colour by that many degrees.
/// All neutral passes the image through without re-encoding.
/// </summary>
[NodeDefinition("Image/Adjust", Description = "Adjusts brightness, contrast, saturation and hue.")]
public sealed class AdjustImageNode : TransformImageNode
{
    private double _brightness = 1;
    private double _contrast = 1;
    private double _saturation = 1;
    private double _hue;

    private readonly InputPort _brightnessIn;
    private readonly InputPort _contrastIn;
    private readonly InputPort _saturationIn;
    private (float Brightness, float Contrast, float Saturation, float Hue) _captured;

    public AdjustImageNode()
    {
        _brightnessIn = AddInput<double>("Brightness");
        _contrastIn = AddInput<double>("Contrast");
        _saturationIn = AddInput<double>("Saturation");
    }

    public double Brightness { get => _brightness; set => Set(ref _brightness, value); }

    public double Contrast { get => _contrast; set => Set(ref _contrast, value); }

    public double Saturation { get => _saturation; set => Set(ref _saturation, value); }

    /// <summary>Degrees to turn the colour wheel, -180 to 180.</summary>
    public double Hue { get => _hue; set => Set(ref _hue, value); }

    protected override void Capture()
    {
        static float Factor(double value) => double.IsNaN(value) ? 1f : (float)Math.Clamp(value, 0, 10);
        _captured = (Factor(_brightnessIn.ValueOr(Brightness)), Factor(_contrastIn.ValueOr(Contrast)),
            Factor(_saturationIn.ValueOr(Saturation)), double.IsNaN(Hue) ? 0f : (float)Math.Clamp(Hue, -180, 180));
    }

    protected override bool Apply(Image image)
    {
        var (brightness, contrast, saturation, hue) = _captured;
        if (brightness == 1 && contrast == 1 && saturation == 1 && hue == 0)
            return false;

        image.Mutate(x =>
        {
            if (brightness != 1)
                x.Brightness(brightness);
            if (contrast != 1)
                x.Contrast(contrast);
            if (saturation != 1)
                x.Saturate(saturation);
            if (hue != 0)
                x.Hue(hue);
        });
        return true;
    }
}

/// <summary>Filters offered by <see cref="FilterImageNode"/>. Saved by number: append, never reorder.</summary>
public enum ImageFilterKind
{
    Grayscale,
    Sepia,
    Invert,

    /// <summary>Gaussian blur; Amount is the radius in pixels.</summary>
    Blur,

    /// <summary>Gaussian sharpen; Amount is the radius in pixels.</summary>
    Sharpen,

    /// <summary>Mosaic; Amount is the block size in pixels.</summary>
    Pixelate,
}

[NodeDefinition("Image/Filter", Description = "Applies a filter: grayscale, sepia, invert, blur, sharpen or pixelate.")]
public sealed class FilterImageNode : TransformImageNode
{
    private ImageFilterKind _filter;
    private double _amount = 3;

    private readonly InputPort _amountIn;
    private (ImageFilterKind Filter, float Amount) _captured;

    public FilterImageNode() => _amountIn = AddInput<double>("Amount");

    public ImageFilterKind Filter { get => _filter; set => Set(ref _filter, value); }

    /// <summary>Radius for blur and sharpen, block size for pixelate; unused by the others.</summary>
    public double Amount { get => _amount; set => Set(ref _amount, value); }

    protected override void Capture()
    {
        var amount = _amountIn.ValueOr(Amount);
        _captured = (Filter, double.IsNaN(amount) ? 0f : (float)Math.Clamp(amount, 0, 500));
    }

    protected override bool Apply(Image image)
    {
        var (filter, amount) = _captured;

        // A zero radius or a one-pixel block changes nothing, so nothing is re-encoded.
        if ((filter is ImageFilterKind.Blur or ImageFilterKind.Sharpen && amount <= 0)
            || (filter == ImageFilterKind.Pixelate && amount < 2))
        {
            return false;
        }

        image.Mutate(x => _ = filter switch
        {
            ImageFilterKind.Sepia => x.Sepia(),
            ImageFilterKind.Invert => x.Invert(),
            ImageFilterKind.Blur => x.GaussianBlur(amount),
            ImageFilterKind.Sharpen => x.GaussianSharpen(amount),
            ImageFilterKind.Pixelate => x.Pixelate((int)amount),
            _ => x.Grayscale(),
        });
        return true;
    }
}

/// <summary>Where <see cref="DrawTextNode"/> places its text. Saved by number: append, never reorder.</summary>
public enum TextPlacement
{
    BottomRight,
    BottomLeft,
    Bottom,
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
}

/// <summary>
/// Draws text on an image: a watermark, a caption, a timestamp. The font is any installed family,
/// falling back to the system's default sans-serif when it is not found. Size is in pixels, and the
/// text keeps Margin pixels from the edges it is placed against.
/// </summary>
[NodeDefinition("Image/Draw Text", Description = "Draws text on an image (watermark, caption, timestamp) in a chosen font, size, colour and position.")]
public sealed class DrawTextNode : TransformImageNode
{
    private string _text = string.Empty;
    private string _font = "Segoe UI";
    private double _fontSize = 32;
    private string _color = "white";
    private double _opacity = 0.8;
    private TextPlacement _placement;
    private double _margin = 16;

    private readonly InputPort _textIn;
    private (string Text, Font Font, Color Color, TextPlacement Placement, float Margin) _captured;

    public DrawTextNode() => _textIn = AddInput("Text", PortTypeCompatibility.Any);

    public string Text { get => _text; set => Set(ref _text, value ?? string.Empty); }

    public string Font { get => _font; set => Set(ref _font, value ?? string.Empty); }

    // Not "Size": that is the node's own size on the canvas, inherited from NodeBase.
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }

    public string Color { get => _color; set => Set(ref _color, value ?? string.Empty); }

    /// <summary>0 is invisible, 1 is solid.</summary>
    public double Opacity { get => _opacity; set => Set(ref _opacity, value); }

    public TextPlacement Placement { get => _placement; set => Set(ref _placement, value); }

    public double Margin { get => _margin; set => Set(ref _margin, value); }

    protected override void Capture()
    {
        var text = _textIn.IsConnected ? NodeValues.ToText(_textIn.Value) : Text;
        var family = SystemFonts.TryGet(Font.Trim(), out var found) ? found
            : SystemFonts.TryGet("Segoe UI", out var fallback) ? fallback
            : SystemFonts.Families.FirstOrDefault();
        if (family == default)
            throw new InvalidOperationException("No fonts are installed to draw with.");

        var size = double.IsNaN(FontSize) ? 32f : (float)Math.Clamp(FontSize, 4, 2000);
        var opacity = double.IsNaN(Opacity) ? 1f : (float)Math.Clamp(Opacity, 0, 1);
        var color = ImageColors.Parse(Color);
        var alpha = color.ToPixel<Rgba32>().A / 255f * opacity;

        _captured = (text, family.CreateFont(size, FontStyle.Regular), color.WithAlpha(alpha), Placement,
            double.IsNaN(Margin) ? 0f : (float)Math.Clamp(Margin, 0, 10_000));
    }

    protected override bool Apply(Image image)
    {
        var (text, font, color, placement, margin) = _captured;
        if (text.Length == 0)
            return false;

        var (horizontal, x) = placement switch
        {
            TextPlacement.TopLeft or TextPlacement.Left or TextPlacement.BottomLeft => (HorizontalAlignment.Left, margin),
            TextPlacement.Top or TextPlacement.Center or TextPlacement.Bottom => (HorizontalAlignment.Center, image.Width / 2f),
            _ => (HorizontalAlignment.Right, image.Width - margin),
        };
        var (vertical, y) = placement switch
        {
            TextPlacement.TopLeft or TextPlacement.Top or TextPlacement.TopRight => (VerticalAlignment.Top, margin),
            TextPlacement.Left or TextPlacement.Center or TextPlacement.Right => (VerticalAlignment.Center, image.Height / 2f),
            _ => (VerticalAlignment.Bottom, image.Height - margin),
        };

        var options = new RichTextOptions(font)
        {
            Origin = new PointF(x, y),
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            TextAlignment = horizontal switch
            {
                HorizontalAlignment.Left => TextAlignment.Start,
                HorizontalAlignment.Right => TextAlignment.End,
                _ => TextAlignment.Center,
            },
        };

        image.Mutate(c => c.DrawText(options, text, color));
        return true;
    }
}

[NodeDefinition("Image/Solid Color", Description = "A new image of one colour, at a size and format: a placeholder, a background, a test image.")]
public sealed class SolidColorImageNode : NodeBase
{
    private double _width = 256;
    private double _height = 256;
    private string _color = "#3080D0";
    private ImageFileFormat _format;

    private readonly InputPort _widthIn;
    private readonly InputPort _heightIn;
    private readonly InputPort _colorIn;
    private readonly OutputPort _out;

    public SolidColorImageNode()
    {
        _widthIn = AddInput<double>("Width");
        _heightIn = AddInput<double>("Height");
        _colorIn = AddInput<string>("Color");
        _out = AddOutput<byte[]>("Image");
    }

    public double Width { get => _width; set => Set(ref _width, value); }

    public double Height { get => _height; set => Set(ref _height, value); }

    /// <summary>A name (white) or hex (#RRGGBB, #RRGGBBAA for transparency).</summary>
    public string Color { get => _color; set => Set(ref _color, value ?? string.Empty); }

    public ImageFileFormat Format { get => _format; set => Set(ref _format, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        static int Pixels(double value) => double.IsNaN(value) ? 1 : (int)Math.Clamp(Math.Round(value), 1, 16_384);

        var width = Pixels(_widthIn.ValueOr(Width));
        var height = Pixels(_heightIn.ValueOr(Height));
        var color = ImageColors.Parse(_colorIn.ValueOr(Color)).ToPixel<Rgba32>();
        var format = Format;

        _out.Value = await Task.Run(() =>
        {
            using var image = new Image<Rgba32>(width, height, color);
            return ImageConversion.Encode(image, format, ImageConversion.DefaultQuality);
        }, context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>An image as a <c>data:</c> URI, ready for an HTML img tag, CSS or a JSON field.</summary>
[NodeDefinition("Image/To Data URI", Description = "An image as a data: URI (data:image/png;base64,…) for HTML, CSS or JSON.")]
public sealed class ImageToDataUriNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _uriOut;
    private readonly OutputPort _mimeOut;

    public ImageToDataUriNode()
    {
        _in = AddInput("Image", PortTypeCompatibility.Any);
        _uriOut = AddOutput<string>("Data URI");
        _mimeOut = AddOutput<string>("MIME Type");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var (bytes, format) = ImageData.Require(_in.Value) ?? throw new InvalidOperationException("No image.");
        var mime = format switch
        {
            "JPEG" => "image/jpeg",
            "TIFF" => "image/tiff",
            "ICO" => "image/x-icon",
            "WebP" => "image/webp",
            "PBM" => "image/x-portable-anymap",
            _ => "image/" + format.ToLowerInvariant(),
        };

        _uriOut.Value = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        _mimeOut.Value = mime;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Reads QR codes and 1D barcodes (EAN, UPC, Code 128, Code 39, ITF, Codabar) and Data Matrix, PDF 417
/// and Aztec, from a photo or a screenshot. Nothing found is a result (Found = false), not an error.
/// </summary>
[NodeDefinition("Encoding/Barcode Read", Description = "Reads QR codes and barcodes (EAN, UPC, Code 128, Data Matrix, PDF 417 and more) from an image.")]
public sealed class BarcodeReadNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _textOut;
    private readonly OutputPort _formatOut;
    private readonly OutputPort _allOut;
    private readonly OutputPort _foundOut;

    public BarcodeReadNode()
    {
        _in = AddInput("Image", PortTypeCompatibility.Any);
        _textOut = AddOutput<string>("Text");
        _formatOut = AddOutput<string>("Format");
        _allOut = AddOutput<string[]>("All");
        _foundOut = AddOutput<bool>("Found");
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var (bytes, _) = ImageData.Require(_in.Value) ?? throw new InvalidOperationException("No image.");

        var results = await Task.Run(() =>
        {
            // ZXing takes raw pixels; handing them over as RGB24 avoids its per-imaging-library bindings.
            using var image = ImageConversion.Load(bytes).CloneAs<Rgb24>();
            var pixels = new byte[image.Width * image.Height * 3];
            image.CopyPixelDataTo(pixels);

            var source = new ZXing.RGBLuminanceSource(pixels, image.Width, image.Height, ZXing.RGBLuminanceSource.BitmapFormat.RGB24);
            var reader = new ZXing.BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = { TryHarder = true, TryInverted = true },
            };

            return reader.DecodeMultiple(source) ?? [];
        }, context.CancellationToken).ConfigureAwait(false);

        _allOut.Value = results.Select(r => r.Text).ToArray();
        _textOut.Value = results.FirstOrDefault()?.Text;
        _formatOut.Value = results.FirstOrDefault()?.BarcodeFormat.ToString();
        _foundOut.Value = results.Length > 0;
    }
}
