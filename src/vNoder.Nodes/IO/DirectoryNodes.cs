using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

// .NET has no asynchronous directory enumeration, so the walk runs on the thread pool and checks for
// cancellation as it goes: a large tree must not stall the UI or outlive a stopped continuous run.
// Folders that cannot be read are skipped rather than failing the whole search, which is what anyone
// searching from a drive root needs.

[NodeDefinition("IO/File Search", Description = "Lists files in a folder matching a pattern such as *.txt, optionally recursing.")]
public sealed class FileSearchNode : NodeBase
{
    private string _directory = string.Empty;
    private string _pattern = "*";
    private bool _recursive;

    private readonly InputPort _directoryIn;
    private readonly InputPort _patternIn;
    private readonly InputPort _recursiveIn;
    private readonly OutputPort _filesOut;
    private readonly OutputPort _countOut;

    public FileSearchNode()
    {
        _directoryIn = AddInput<string>("Directory");
        _patternIn = AddInput<string>("Pattern");
        _recursiveIn = AddInput<bool>("Recursive");
        _filesOut = AddOutput<string[]>("Files");
        _countOut = AddOutput<double>("Count");

        AddRunGate();
    }

    public string Directory
    {
        get => _directory;
        set => Set(ref _directory, value ?? string.Empty);
    }

    /// <summary>File-name pattern with <c>*</c> and <c>?</c>, e.g. <c>*.log</c>. Empty means all files.</summary>
    public string Pattern
    {
        get => _pattern;
        set => Set(ref _pattern, value ?? string.Empty);
    }

    public bool Recursive
    {
        get => _recursive;
        set => Set(ref _recursive, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var directory = DirectoryPaths.Require(_directoryIn.IsConnected ? _directoryIn.ValueOr(string.Empty) : Directory);
        var pattern = _patternIn.ValueOr(Pattern);
        var options = DirectoryPaths.Options(_recursiveIn.ValueOr(Recursive));
        var token = context.CancellationToken;

        var files = await Task.Run(
                () =>
                {
                    var found = new List<string>();
                    foreach (var file in System.IO.Directory.EnumerateFiles(
                                 directory,
                                 string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim(),
                                 options))
                    {
                        token.ThrowIfCancellationRequested();
                        found.Add(file);
                    }

                    found.Sort(StringComparer.OrdinalIgnoreCase);
                    return found.ToArray();
                },
                token)
            .ConfigureAwait(false);

        _filesOut.Value = files;
        _countOut.Value = (double)files.Length;
    }
}

[NodeDefinition("IO/Directory Size", Description = "Total size of the files in a folder, optionally including subfolders.")]
public sealed class DirectorySizeNode : NodeBase
{
    private string _directory = string.Empty;
    private bool _recursive = true;

    private readonly InputPort _directoryIn;
    private readonly InputPort _recursiveIn;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _filesOut;
    private readonly OutputPort _textOut;

    public DirectorySizeNode()
    {
        _directoryIn = AddInput<string>("Directory");
        _recursiveIn = AddInput<bool>("Recursive");
        _bytesOut = AddOutput<double>("Bytes");
        _filesOut = AddOutput<double>("Files");
        _textOut = AddOutput<string>("Size");

        AddRunGate();
    }

    public string Directory
    {
        get => _directory;
        set => Set(ref _directory, value ?? string.Empty);
    }

    public bool Recursive
    {
        get => _recursive;
        set => Set(ref _recursive, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var directory = DirectoryPaths.Require(_directoryIn.IsConnected ? _directoryIn.ValueOr(string.Empty) : Directory);
        var options = DirectoryPaths.Options(_recursiveIn.ValueOr(Recursive));
        var token = context.CancellationToken;

        var (bytes, files) = await Task.Run(
                () =>
                {
                    long total = 0;
                    long count = 0;
                    foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
                    {
                        token.ThrowIfCancellationRequested();
                        total += file.Length;
                        count++;
                    }

                    return (total, count);
                },
                token)
            .ConfigureAwait(false);

        _bytesOut.Value = (double)bytes;
        _filesOut.Value = (double)files;
        _textOut.Value = HumanSize(bytes);
    }

    private static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{size.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

internal static class DirectoryPaths
{
    public static string Require(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("No directory set.");

        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!System.IO.Directory.Exists(path))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        return path;
    }

    public static EnumerationOptions Options(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        IgnoreInaccessible = true,

        // Following links and junctions can loop (a junction to an ancestor) or double-count.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };
}
