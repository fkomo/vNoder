using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Re-encodes an image in another format: PNG to JPEG, anything to WebP, and so on. The output is
/// bytes, so it wires straight into Image (to look at), Image Save (to keep) or Base64 Encode.
/// </summary>
[NodeDefinition("Image/Convert", Description = "Converts an image to PNG, JPEG, WebP, GIF, BMP, TIFF, TGA, QOI or PBM.")]
public sealed class ConvertImageNode : NodeBase
{
    private ImageFileFormat _format = ImageFileFormat.PNG;
    private double _quality = ImageConversion.DefaultQuality;

    private readonly InputPort _imageIn;
    private readonly InputPort _qualityIn;
    private readonly OutputPort _imageOut;
    private readonly OutputPort _formatOut;

    public ConvertImageNode()
    {
        _imageIn = AddInput("Image", PortTypeCompatibility.Any);
        _qualityIn = AddInput<double>("Quality");
        _imageOut = AddOutput<byte[]>("Image");
        _formatOut = AddOutput<string>("Format");
    }

    public ImageFileFormat Format
    {
        get => _format;
        set => Set(ref _format, value);
    }

    /// <summary>1–100, for JPEG and WebP; WebP at 100 is lossless. Ignored by the other formats.</summary>
    public double Quality
    {
        get => _quality;
        set => Set(ref _quality, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        _imageOut.Value = null;
        _formatOut.Value = null;

        if (ImageData.Require(_imageIn.Value) is not { } image)
            return;

        var format = Format;
        var quality = _qualityIn.ValueOr(Quality);
        var token = context.CancellationToken;

        // Decoding and encoding are CPU-bound; off the evaluation path like the password hashes.
        _imageOut.Value = await Task.Run(
                () => ImageConversion.Convert(image.Bytes, format, double.IsNaN(quality) ? ImageConversion.DefaultQuality : (int)quality, token),
                token)
            .ConfigureAwait(false);
        _formatOut.Value = ImageConversion.Name(format);
    }
}
