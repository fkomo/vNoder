using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// The lines added to a file since the previous pass: <c>tail -f</c> for a continuous run. Into Regex
/// Match or List Filter and then Notify, it watches a log for errors without re-reading what was seen.
/// </summary>
/// <remarks>
/// <para>
/// The first pass of a run starts at the end of the file and gives nothing, unless From Start is
/// ticked, so a monitor reacts to what is written from now on. A line still being written (no newline
/// yet) is held back until it is complete. A file that shrank was rotated or truncated, and is read
/// again from the beginning. It is opened shared, so the program writing it is not disturbed.
/// </para>
/// <para>
/// A file that does not exist (yet) is a result, not an error: Exists is false and there are no lines,
/// and reading starts from its beginning once it appears. Read as UTF-8.
/// </para>
/// </remarks>
[NodeDefinition("IO/File Tail", Description = "The lines added to a file since the previous pass, like tail -f — for watching a log in a continuous run.")]
public sealed class FileTailNode : StatefulNode
{
    /// <summary>The most read in one pass, so a huge first read or a runaway log cannot stall a run.</summary>
    private const int MaxBytesPerPass = 16 * 1024 * 1024;

    private string _path = string.Empty;
    private bool _fromStart;

    private string? _file;
    private long? _position;

    private readonly InputPort _pathIn;
    private readonly OutputPort _linesOut;
    private readonly OutputPort _textOut;
    private readonly OutputPort _countOut;
    private readonly OutputPort _existsOut;

    public FileTailNode()
    {
        _pathIn = AddInput<string>("Path");
        _linesOut = AddOutput<string[]>("Lines");
        _textOut = AddOutput<string>("Text");
        _countOut = AddOutput<double>("Count");
        _existsOut = AddOutput<bool>("Exists");

        AddRunGate();
    }

    public string Path { get => _path; set => Set(ref _path, value ?? string.Empty); }

    /// <summary>Whether the first pass reads what the file already holds, rather than starting at its end.</summary>
    public bool FromStart { get => _fromStart; set => Set(ref _fromStart, value); }

    protected override void Reset()
    {
        _file = null;
        _position = null;
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        Remember(context);

        var path = (_pathIn.IsConnected ? _pathIn.ValueOr(string.Empty) : Path).Trim();
        if (path.Length == 0)
            throw new InvalidOperationException("No file path set.");

        var full = System.IO.Path.GetFullPath(path);
        if (!string.Equals(full, _file, StringComparison.OrdinalIgnoreCase))
        {
            // A different file starts over, as a new run does.
            _file = full;
            _position = null;
        }

        string[] lines = [];
        var exists = File.Exists(full);
        if (exists)
        {
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, useAsync: true);

            var length = stream.Length;
            var start = _position switch
            {
                null => FromStart ? 0 : length,
                { } position when position > length => 0,
                { } position => position,
            };

            var count = (int)Math.Min(length - start, MaxBytesPerPass);
            var buffer = new byte[count];
            stream.Position = start;
            await stream.ReadExactlyAsync(buffer, context.CancellationToken).ConfigureAwait(false);

            // Only complete lines: whatever follows the last newline waits for the next pass.
            var end = Array.LastIndexOf(buffer, (byte)'\n');
            _position = start + end + 1;
            var bom = start == 0 && end >= 2 && buffer is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
            if (end >= 0)
                lines = [.. Encoding.UTF8.GetString(buffer, bom, end - bom).Split('\n').Select(l => l.TrimEnd('\r'))];
        }
        else
        {
            // Not there yet: read it from its first byte when it appears.
            _position = 0;
        }

        _linesOut.Value = lines;
        _textOut.Value = string.Join(Environment.NewLine, lines);
        _countOut.Value = (double)lines.Length;
        _existsOut.Value = exists;
    }
}
