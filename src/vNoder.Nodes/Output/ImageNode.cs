using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Shows an image. Universal in what it accepts: encoded image bytes of any common format (from QR
/// Code, File Read, HTTP Request), or text holding Base64 or a <c>data:</c> URI.
/// </summary>
/// <remarks>
/// Core and Nodes carry no UI reference, so decoding to pixels is the view's job
/// (<c>BytesToImageConverter</c>). What the node does is recognise the format and size from the
/// headers (<see cref="ImageData"/>), so that input which is not an image is reported as an ordinary
/// node error rather than as a silently blank preview. The recognised bytes are passed on, which
/// lets a Base64 string be turned into a file's worth of bytes on the way through.
/// </remarks>
[NodeDefinition(
    "Output/Image",
    Description = "Shows an image: PNG, JPEG, GIF, BMP, TIFF, ICO, WebP, TGA, QOI or PBM bytes, or Base64 / data-URI text.",
    Resizable = true)]
public sealed class ImageNode : NodeBase
{
    private bool _pixelated;

    private readonly InputPort _in;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _formatOut;
    private readonly OutputPort _widthOut;
    private readonly OutputPort _heightOut;

    public ImageNode()
    {
        _in = AddInput("Image", PortTypeCompatibility.Any);
        _bytesOut = AddOutput<byte[]>("Bytes");
        _formatOut = AddOutput<string>("Format");
        _widthOut = AddOutput<double>("Width");
        _heightOut = AddOutput<double>("Height");
    }

    /// <summary>
    /// Scale without smoothing, so pixel art and QR codes stay crisp. Off, photos scale smoothly.
    /// </summary>
    public bool Pixelated
    {
        get => _pixelated;
        set => Set(ref _pixelated, value);
    }

    /// <summary>The last image's bytes. Computed, so not persisted.</summary>
    public byte[]? Bytes { get; private set; }

    /// <summary>
    /// What the preview shows: the image itself, or a PNG rendering of it for a format WPF cannot be
    /// relied on to decode. Computed, so not persisted.
    /// </summary>
    public byte[]? Preview { get; private set; }

    /// <summary>
    /// Formats the view could not show directly. WPF has no TGA, QOI or PBM decoder, and it decodes
    /// WebP only through an optional Windows codec that N editions and Server lack.
    /// </summary>
    private static readonly HashSet<string> PreviewAsPng = ["TGA", "QOI", "PBM", "WebP"];

    /// <summary>The recognised format, e.g. "PNG". Computed, so not persisted.</summary>
    public string? ImageFormat { get; private set; }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Cleared first, so a failure does not leave the previous image looking current.
        Bytes = null;
        Preview = null;
        ImageFormat = null;
        foreach (var output in Outputs)
            output.Value = null;

        if (ImageData.Require(_in.Value) is not { } image)
            return;

        var (bytes, format) = image;

        Bytes = bytes;
        ImageFormat = format;
        _bytesOut.Value = bytes;
        _formatOut.Value = format;

        if (ImageData.Dimensions(bytes) is { } size)
        {
            _widthOut.Value = (double)size.Width;
            _heightOut.Value = (double)size.Height;
        }

        var token = context.CancellationToken;
        Preview = PreviewAsPng.Contains(format)
            ? await Task.Run(() => ImageConversion.Convert(bytes, ImageFileFormat.PNG, 100, token), token).ConfigureAwait(false)
            : bytes;
    }
}
