using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("IO/File Write", Description = "Writes text or bytes to a file, creating its folder if needed.")]
public sealed class FileWriteNode : NodeBase
{
    private string _path = string.Empty;
    private bool _appendIfExists;

    private readonly InputPort _pathIn;
    private readonly InputPort _dataIn;
    private readonly OutputPort _successOut;

    public FileWriteNode()
    {
        _pathIn = AddInput<string>("Path");
        _dataIn = AddInput("Data", PortTypeCompatibility.Any);
        _successOut = AddOutput<bool>("Success");
    }

    /// <summary>Inline path, used when the Path input is not wired.</summary>
    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    /// <summary>Append to the file rather than replacing it. Creates the file if it is not there.</summary>
    public bool AppendIfExists
    {
        get => _appendIfExists;
        set => Set(ref _appendIfExists, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        _successOut.Value = false;

        var path = _pathIn.IsConnected ? _pathIn.ValueOr(string.Empty) : Path;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("No file path set.");

        byte[] bytes = _dataIn.Value switch
        {
            null => Array.Empty<byte>(),
            byte[] raw => raw,
            string text => Encoding.UTF8.GetBytes(text),
            string[] parts => Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, parts)),
            var other => Encoding.UTF8.GetBytes(Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
        };

        FilePaths.EnsureDirectory(path);

        if (AppendIfExists)
            await File.AppendAllBytesAsync(path, bytes, context.CancellationToken).ConfigureAwait(false);
        else
            await File.WriteAllBytesAsync(path, bytes, context.CancellationToken).ConfigureAwait(false);
        _successOut.Value = true;
    }
}
