using vNoder.Core;

namespace vNoder.Nodes;

// Looking at paths, and taking them apart. The Path nodes only manipulate text and never touch the
// disk; the IO ones read the file system but, apart from Create Directory, change nothing, so they are
// safe in a continuous run.

internal static class PathText
{
    /// <summary>Trimmed, unquoted, with %VARIABLES% expanded; empty when there is no path.</summary>
    public static string Clean(string path) =>
        Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
}

[NodeDefinition("IO/Path Exists", Description = "Whether a file or directory exists at a path.")]
public sealed class PathExistsNode : NodeBase
{
    private string _path = string.Empty;

    private readonly InputPort _pathIn;
    private readonly OutputPort _existsOut;
    private readonly OutputPort _isFileOut;
    private readonly OutputPort _isDirectoryOut;

    public PathExistsNode()
    {
        _pathIn = AddInput<string>("Path");
        _existsOut = AddOutput<bool>("Exists");
        _isFileOut = AddOutput<bool>("Is File");
        _isDirectoryOut = AddOutput<bool>("Is Directory");

        AddRunGate();
    }

    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // An empty path does not exist; it is not an error.
        var path = PathText.Clean(_pathIn.ValueOr(Path));
        var isFile = path.Length > 0 && File.Exists(path);
        var isDirectory = path.Length > 0 && !isFile && Directory.Exists(path);

        _existsOut.Value = isFile || isDirectory;
        _isFileOut.Value = isFile;
        _isDirectoryOut.Value = isDirectory;
        return Task.CompletedTask;
    }
}

[NodeDefinition("IO/File Info", Description = "Size, created and modified times, and read-only flag of a file or directory.")]
public sealed class FileInfoNode : NodeBase
{
    private string _path = string.Empty;

    private readonly InputPort _pathIn;
    private readonly OutputPort _existsOut;
    private readonly OutputPort _sizeOut;
    private readonly OutputPort _createdOut;
    private readonly OutputPort _modifiedOut;
    private readonly OutputPort _readOnlyOut;

    public FileInfoNode()
    {
        _pathIn = AddInput<string>("Path");
        _existsOut = AddOutput<bool>("Exists");
        _sizeOut = AddOutput<double>("Size");
        _createdOut = AddOutput<DateTime>("Created");
        _modifiedOut = AddOutput<DateTime>("Modified");
        _readOnlyOut = AddOutput<bool>("Read Only");

        AddRunGate();
    }

    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _existsOut.Value = false;
        _sizeOut.Value = null;
        _createdOut.Value = null;
        _modifiedOut.Value = null;
        _readOnlyOut.Value = null;

        var path = PathText.Clean(_pathIn.ValueOr(Path));
        if (path.Length == 0)
            return Task.CompletedTask;

        FileSystemInfo info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);

        // A missing path is a result (Exists = false), like Path Exists, not an error.
        if (!info.Exists)
            return Task.CompletedTask;

        _existsOut.Value = true;

        // A directory's size is Directory Size's job (it has to walk the tree); here it is left empty.
        _sizeOut.Value = info is FileInfo file ? (double)file.Length : null;
        _createdOut.Value = info.CreationTime;
        _modifiedOut.Value = info.LastWriteTime;
        _readOnlyOut.Value = info.Attributes.HasFlag(FileAttributes.ReadOnly);
        return Task.CompletedTask;
    }
}

[NodeDefinition("IO/Create Directory", Description = "Creates a directory and any missing parents; nothing to do if it exists.")]
public sealed class CreateDirectoryNode : NodeBase
{
    private string _path = string.Empty;

    private readonly InputPort _pathIn;
    private readonly OutputPort _pathOut;
    private readonly OutputPort _createdOut;

    public CreateDirectoryNode()
    {
        _pathIn = AddInput<string>("Path");
        _pathOut = AddOutput<string>("Path");
        _createdOut = AddOutput<bool>("Created");

        AddRunGate();
    }

    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _pathOut.Value = null;
        _createdOut.Value = false;

        var path = PathText.Clean(_pathIn.ValueOr(Path));
        if (path.Length == 0)
            throw new InvalidOperationException("No directory path set.");

        path = System.IO.Path.GetFullPath(path);
        if (File.Exists(path))
            throw new InvalidOperationException($"{path} is a file.");

        // Created reports whether this pass made it, as Path Delete's Deleted does, so an existing
        // directory does not read as something done.
        var existed = Directory.Exists(path);
        Directory.CreateDirectory(path);

        _pathOut.Value = path;
        _createdOut.Value = !existed;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Path/Path Parts", Description = "Splits a path into directory, file name, name without extension, extension and full path.")]
public sealed class PathPartsNode : NodeBase
{
    private string _path = string.Empty;

    private readonly InputPort _pathIn;
    private readonly OutputPort _directoryOut;
    private readonly OutputPort _fileNameOut;
    private readonly OutputPort _nameOut;
    private readonly OutputPort _extensionOut;
    private readonly OutputPort _fullOut;

    public PathPartsNode()
    {
        _pathIn = AddInput<string>("Path");
        _directoryOut = AddOutput<string>("Directory");
        _fileNameOut = AddOutput<string>("File Name");
        _nameOut = AddOutput<string>("Name");
        _extensionOut = AddOutput<string>("Extension");
        _fullOut = AddOutput<string>("Full Path");
    }

    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var path = PathText.Clean(_pathIn.ValueOr(Path));

        _directoryOut.Value = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        _fileNameOut.Value = System.IO.Path.GetFileName(path);
        _nameOut.Value = System.IO.Path.GetFileNameWithoutExtension(path);
        _extensionOut.Value = System.IO.Path.GetExtension(path);
        _fullOut.Value = path.Length == 0 ? string.Empty : System.IO.Path.GetFullPath(path);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Path/Combine", Description = "Joins folder and file names into one path; an absolute part starts over from there.")]
public sealed class PathCombineNode : NodeBase
{
    private string _a = string.Empty;
    private string _b = string.Empty;
    private string _c = string.Empty;

    private readonly InputPort _aIn;
    private readonly InputPort _bIn;
    private readonly InputPort _cIn;
    private readonly OutputPort _out;

    public PathCombineNode()
    {
        _aIn = AddInput<string>("A");
        _bIn = AddInput<string>("B");
        _cIn = AddInput<string>("C");
        _out = AddOutput<string>("Path");
    }

    public string A
    {
        get => _a;
        set => Set(ref _a, value ?? string.Empty);
    }

    public string B
    {
        get => _b;
        set => Set(ref _b, value ?? string.Empty);
    }

    public string C
    {
        get => _c;
        set => Set(ref _c, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Path.Combine skips empty parts, so an unused C adds nothing.
        _out.Value = System.IO.Path.Combine(
            PathText.Clean(_aIn.ValueOr(A)),
            PathText.Clean(_bIn.ValueOr(B)),
            PathText.Clean(_cIn.ValueOr(C)));
        return Task.CompletedTask;
    }
}

[NodeDefinition("Path/Change Extension", Description = "Replaces a path's extension, or removes it when the new one is empty.")]
public sealed class ChangeExtensionNode : NodeBase
{
    private string _extension = ".txt";

    private readonly InputPort _pathIn;
    private readonly InputPort _extensionIn;
    private readonly OutputPort _out;

    public ChangeExtensionNode()
    {
        _pathIn = AddInput<string>("Path");
        _extensionIn = AddInput<string>("Extension");
        _out = AddOutput<string>("Path");
    }

    /// <summary>With or without the dot; empty removes the extension.</summary>
    public string Extension
    {
        get => _extension;
        set => Set(ref _extension, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var path = PathText.Clean(_pathIn.ValueOr(string.Empty));
        var extension = _extensionIn.ValueOr(Extension).Trim();

        _out.Value = path.Length == 0
            ? string.Empty
            : System.IO.Path.ChangeExtension(path, extension.Length == 0 ? null : extension);
        return Task.CompletedTask;
    }
}
