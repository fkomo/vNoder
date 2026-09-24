using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Copies or moves a file. The destination may name the new file or an existing folder to put it in
/// (keeping the source's name); missing parent folders are created. Like File Write, this acts on every
/// pass of a continuous run, which is what <see cref="ExistingFileMode.AddNumber"/> is for.
/// </summary>
public abstract class FileTransferNode : NodeBase
{
    private string _source = string.Empty;
    private string _destination = string.Empty;
    private ExistingFileMode _ifExists;

    private readonly InputPort _sourceIn;
    private readonly InputPort _destinationIn;
    private readonly OutputPort _successOut;
    private readonly OutputPort _pathOut;

    protected FileTransferNode(string resultName)
    {
        _sourceIn = AddInput<string>("Source");
        _destinationIn = AddInput<string>("Destination");
        _successOut = AddOutput<bool>("Success");
        _pathOut = AddOutput<string>(resultName);
    }

    /// <summary>Inline source path, used when the Source input is not wired.</summary>
    public string Source
    {
        get => _source;
        set => Set(ref _source, value ?? string.Empty);
    }

    /// <summary>Inline destination: a file path, or a folder to put the file in. Used when Destination is not wired.</summary>
    public string Destination
    {
        get => _destination;
        set => Set(ref _destination, value ?? string.Empty);
    }

    public ExistingFileMode IfExists
    {
        get => _ifExists;
        set => Set(ref _ifExists, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        _successOut.Value = false;
        _pathOut.Value = null;

        var source = Resolve(_sourceIn.ValueOr(Source), "No source path set.");
        var destination = Resolve(_destinationIn.ValueOr(Destination), "No destination path set.");

        if (!File.Exists(source))
        {
            throw new InvalidOperationException(Directory.Exists(source)
                ? $"{source} is a directory; {Title} works on files."
                : $"Source file not found: {source}");
        }

        // An existing folder, or a path written with a trailing separator, means "put it in there".
        var raw = _destinationIn.ValueOr(Destination).Trim().Trim('"');
        if (Directory.Exists(destination) || System.IO.Path.EndsInDirectorySeparator(raw))
            destination = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(source));

        if (File.Exists(destination))
        {
            switch (IfExists)
            {
                case ExistingFileMode.Skip:
                    _pathOut.Value = destination;
                    return;

                case ExistingFileMode.AddNumber:
                    destination = FilePaths.NextFreeName(destination);
                    break;

                case ExistingFileMode.Overwrite when string.Equals(source, destination, StringComparison.OrdinalIgnoreCase):
                    throw new InvalidOperationException("Source and destination are the same file.");
            }
        }

        FilePaths.EnsureDirectory(destination);

        // Neither File.Copy nor File.Move has an async form, and a large file (or a move across
        // volumes, which is a copy) takes a while, so keep it off the evaluation path.
        var token = context.CancellationToken;
        await Task.Run(() => Transfer(source, destination), token).ConfigureAwait(false);
        _successOut.Value = true;
        _pathOut.Value = destination;
    }

    /// <summary>Copies or moves; the destination has been decided, and replacing it is allowed.</summary>
    protected abstract void Transfer(string source, string destination);

    private static string Resolve(string path, string missing)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0)
            throw new InvalidOperationException(missing);

        return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }
}

[NodeDefinition("IO/File Copy", Description = "Copies a file to a new path or into a folder, creating the folder if needed.")]
public sealed class FileCopyNode : FileTransferNode
{
    public FileCopyNode() : base("Copied To") { }

    protected override void Transfer(string source, string destination) =>
        File.Copy(source, destination, overwrite: true);
}

[NodeDefinition("IO/File Move", Description = "Moves or renames a file, to a new path or into a folder, creating the folder if needed.")]
public sealed class FileMoveNode : FileTransferNode
{
    public FileMoveNode() : base("Moved To") { }

    protected override void Transfer(string source, string destination) =>
        File.Move(source, destination, overwrite: true);
}
