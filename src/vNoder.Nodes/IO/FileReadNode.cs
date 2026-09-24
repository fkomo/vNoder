using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("IO/File Read", Description = "Reads a file as bytes and as UTF-8 text.")]
public sealed class FileReadNode : NodeBase
{
    private string _path = string.Empty;
    private readonly InputPort _pathIn;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _textOut;

    public FileReadNode()
    {
        _pathIn = AddInput<string>("Path");
        _bytesOut = AddOutput<byte[]>("Bytes");
        _textOut = AddOutput<string>("Text");

        AddRunGate();
    }

    /// <summary>Inline path, used when the Path input is not wired.</summary>
    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var path = _pathIn.IsConnected ? _pathIn.ValueOr(string.Empty) : Path;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("No file path set.");

        var bytes = await File.ReadAllBytesAsync(path, context.CancellationToken).ConfigureAwait(false);
        _bytesOut.Value = bytes;
        _textOut.Value = Encoding.UTF8.GetString(bytes);
    }
}
