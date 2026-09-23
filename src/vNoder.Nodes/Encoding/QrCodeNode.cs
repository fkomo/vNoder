using QRCoder;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Error-correction levels, from most compact to most damage-tolerant.</summary>
public enum QrErrorCorrection
{
    L,
    M,
    Q,
    H,
}

/// <summary>
/// Renders text as a QR code: a PNG on the output, and the same image in the node body so the code
/// can be checked (or scanned off the screen) without wiring anything to a file.
/// </summary>
[NodeDefinition("Encoding/QR Code", Description = "Renders text as a QR code PNG, previewed in the node.", Resizable = true)]
public sealed class QrCodeNode : NodeBase
{
    private QrErrorCorrection _errorCorrection = QrErrorCorrection.M;
    private double _pixelsPerModule = 10;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public QrCodeNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<byte[]>("PNG");
    }

    public QrErrorCorrection ErrorCorrection
    {
        get => _errorCorrection;
        set => Set(ref _errorCorrection, value);
    }

    /// <summary>Size of one module (one black or white square) in the PNG, in pixels.</summary>
    public double PixelsPerModule
    {
        get => _pixelsPerModule;
        set => Set(ref _pixelsPerModule, value);
    }

    /// <summary>
    /// The last image, for the preview. Assigned during evaluation without notifying, like
    /// <see cref="OutputNode.Value"/>; not persisted, since it is computed.
    /// </summary>
    public byte[]? Png { get; private set; }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);
        if (text.Length == 0)
        {
            Png = null;
            _out.Value = null;
            return Task.CompletedTask;
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, Level(ErrorCorrection));

        // Clamped: the preview is scaled anyway, and a huge module size only makes a huge file.
        var png = new PngByteQRCode(data).GetGraphic((int)Math.Clamp(PixelsPerModule, 1, 40));

        Png = png;
        _out.Value = png;
        return Task.CompletedTask;
    }

    private static QRCodeGenerator.ECCLevel Level(QrErrorCorrection level) => level switch
    {
        QrErrorCorrection.L => QRCodeGenerator.ECCLevel.L,
        QrErrorCorrection.Q => QRCodeGenerator.ECCLevel.Q,
        QrErrorCorrection.H => QRCodeGenerator.ECCLevel.H,
        _ => QRCodeGenerator.ECCLevel.M,
    };
}
