using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// An edit to an image that keeps its format: a PNG stays a PNG, a JPEG keeps its quality. Chain
/// Convert after it for a different format. Decoding and encoding are CPU-bound, so like Convert this
/// runs off the evaluation path.
/// </summary>
public abstract class TransformImageNode : NodeBase
{
    private readonly InputPort _imageIn;
    private readonly OutputPort _imageOut;
    private readonly OutputPort _widthOut;
    private readonly OutputPort _heightOut;

    protected TransformImageNode()
    {
        _imageIn = AddInput("Image", PortTypeCompatibility.Any);
        _imageOut = AddOutput<byte[]>("Image");
        _widthOut = AddOutput<double>("Width");
        _heightOut = AddOutput<double>("Height");
    }

    /// <summary>Called on the thread pool. Returns false when the image needs no change.</summary>
    protected abstract bool Apply(Image image);

    /// <summary>Reads the wired or inline parameters on the evaluation thread, before <see cref="Apply"/>.</summary>
    protected virtual void Capture()
    {
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        _imageOut.Value = null;
        _widthOut.Value = null;
        _heightOut.Value = null;

        if (ImageData.Require(_imageIn.Value) is not { } image)
            return;

        Capture();
        var token = context.CancellationToken;
        var (bytes, width, height) = await Task.Run(() => ImageConversion.Transform(image.Bytes, Apply, token), token)
            .ConfigureAwait(false);

        _imageOut.Value = bytes;
        _widthOut.Value = (double)width;
        _heightOut.Value = (double)height;
    }

    protected static int Pixels(double value) =>
        double.IsNaN(value) ? 0 : (int)Math.Clamp(Math.Round(value), 0, 65_535);
}

public enum ImageFit
{
    /// <summary>Scale to fit inside the box, keeping the aspect ratio: the thumbnail case.</summary>
    Fit,

    /// <summary>Scale to cover the box and crop the overflow, keeping the aspect ratio.</summary>
    Fill,

    /// <summary>Scale to exactly the box, distorting if the aspect ratio differs.</summary>
    Stretch,

    /// <summary>Fit inside the box, then pad to exactly its size (transparent, or white without alpha).</summary>
    Pad,
}

[NodeDefinition("Image/Resize", Description = "Resizes an image or makes a thumbnail: fit, fill (crop), stretch or pad to a size.")]
public sealed class ResizeImageNode : TransformImageNode
{
    private double _width = 256;
    private double _height = 256;
    private ImageFit _fit;
    private bool _onlyShrink = true;

    private readonly InputPort _widthIn;
    private readonly InputPort _heightIn;
    private int _targetWidth;
    private int _targetHeight;

    public ResizeImageNode()
    {
        _widthIn = AddInput<double>("Width");
        _heightIn = AddInput<double>("Height");
    }

    /// <summary>Target width in pixels; 0 follows the height, keeping the aspect ratio.</summary>
    public double Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    /// <summary>Target height in pixels; 0 follows the width, keeping the aspect ratio.</summary>
    public double Height
    {
        get => _height;
        set => Set(ref _height, value);
    }

    public ImageFit Fit
    {
        get => _fit;
        set => Set(ref _fit, value);
    }

    /// <summary>Leave an image that already fits alone, rather than enlarging it.</summary>
    public bool OnlyShrink
    {
        get => _onlyShrink;
        set => Set(ref _onlyShrink, value);
    }

    protected override void Capture()
    {
        _targetWidth = Pixels(_widthIn.ValueOr(Width));
        _targetHeight = Pixels(_heightIn.ValueOr(Height));
    }

    protected override bool Apply(Image image)
    {
        var width = _targetWidth;
        var height = _targetHeight;
        if (width == 0 && height == 0)
            return false;

        // A missing side follows the other one, so the aspect ratio holds whatever the fit.
        if (width == 0)
            width = Math.Max(1, (int)Math.Round((double)image.Width * height / image.Height));
        else if (height == 0)
            height = Math.Max(1, (int)Math.Round((double)image.Height * width / image.Width));

        if (OnlyShrink && image.Width <= width && image.Height <= height)
            return false;

        if (image.Width == width && image.Height == height)
            return false;

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = Fit switch
            {
                ImageFit.Fill => ResizeMode.Crop,
                ImageFit.Stretch => ResizeMode.Stretch,
                ImageFit.Pad => ResizeMode.Pad,
                _ => ResizeMode.Max,
            },
            PadColor = Color.Transparent,
        }));
        return true;
    }
}

[NodeDefinition("Image/Crop", Description = "Cuts out a rectangle of an image, given its left, top, width and height.")]
public sealed class CropImageNode : TransformImageNode
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;

    private readonly InputPort _xIn;
    private readonly InputPort _yIn;
    private readonly InputPort _widthIn;
    private readonly InputPort _heightIn;
    private Rectangle _area;

    public CropImageNode()
    {
        _xIn = AddInput<double>("X");
        _yIn = AddInput<double>("Y");
        _widthIn = AddInput<double>("Width");
        _heightIn = AddInput<double>("Height");
    }

    public double X
    {
        get => _x;
        set => Set(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => Set(ref _y, value);
    }

    /// <summary>0 reaches the right edge.</summary>
    public double Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    /// <summary>0 reaches the bottom edge.</summary>
    public double Height
    {
        get => _height;
        set => Set(ref _height, value);
    }

    protected override void Capture() =>
        _area = new Rectangle(
            Pixels(_xIn.ValueOr(X)),
            Pixels(_yIn.ValueOr(Y)),
            Pixels(_widthIn.ValueOr(Width)),
            Pixels(_heightIn.ValueOr(Height)));

    protected override bool Apply(Image image)
    {
        // Clamped to the image rather than thrown, like Substring, so a rectangle being dragged past
        // the edge mid-edit still yields the part that overlaps.
        var x = Math.Min(_area.X, image.Width - 1);
        var y = Math.Min(_area.Y, image.Height - 1);
        var width = _area.Width == 0 ? image.Width - x : Math.Min(_area.Width, image.Width - x);
        var height = _area.Height == 0 ? image.Height - y : Math.Min(_area.Height, image.Height - y);

        if (x == 0 && y == 0 && width == image.Width && height == image.Height)
            return false;

        image.Mutate(c => c.Crop(new Rectangle(x, y, width, height)));
        return true;
    }
}

public enum ImageOrientation
{
    Rotate90,
    Rotate180,
    Rotate270,
    FlipHorizontal,
    FlipVertical,

    /// <summary>Apply the camera's EXIF orientation, so a phone photo stands the right way up everywhere.</summary>
    AutoOrient,
}

[NodeDefinition("Image/Rotate Flip", Description = "Rotates an image by 90° steps, mirrors it, or straightens a photo by its EXIF orientation.")]
public sealed class RotateFlipImageNode : TransformImageNode
{
    private ImageOrientation _operation;

    public ImageOrientation Operation
    {
        get => _operation;
        set => Set(ref _operation, value);
    }

    protected override bool Apply(Image image)
    {
        var operation = Operation;
        image.Mutate(x => _ = operation switch
        {
            ImageOrientation.Rotate180 => x.Rotate(RotateMode.Rotate180),
            ImageOrientation.Rotate270 => x.Rotate(RotateMode.Rotate270),
            ImageOrientation.FlipHorizontal => x.Flip(FlipMode.Horizontal),
            ImageOrientation.FlipVertical => x.Flip(FlipMode.Vertical),
            ImageOrientation.AutoOrient => x.AutoOrient(),
            _ => x.Rotate(RotateMode.Rotate90),
        });
        return true;
    }
}
