using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Deletes a file or a directory. Destructive and unconditional: wired into a continuous run it will
/// fire on every tick, which is the user's call to make.
/// </summary>
/// <remarks>
/// Formerly "IO/File Delete"; <see cref="NodeDefinitionAttribute.FormerPaths"/> keeps saved graphs
/// loading. A directory with anything in it is deleted only when <see cref="Recursive"/> is set, so a
/// path mistyped as a folder cannot silently take a tree with it. A drive root is always refused.
/// <para>
/// Links are removed, never followed: a junction or symbolic link inside the tree is unlinked and its
/// target left alone, and a path that is itself a link has just the link removed (no Recursive needed,
/// since nothing behind it is touched). The walk is done here rather than by
/// <c>Directory.Delete(path, true)</c>, which does not follow junctions either but fails with "access
/// denied" on reaching one, leaving the tree half there. To test it, use a real junction
/// (<c>mklink /J</c>, no admin needed) with a sentinel file in its target.
/// </para>
/// </remarks>
[NodeDefinition(
    "IO/Path Delete",
    Description = "Deletes a file or a directory (with its contents only when Recursive is ticked).",
    FormerPaths = ["IO/File Delete"])]
public sealed class PathDeleteNode : NodeBase
{
    private string _path = string.Empty;
    private bool _recursive;

    private readonly InputPort _pathIn;
    private readonly OutputPort _deletedOut;

    public PathDeleteNode()
    {
        _pathIn = AddInput<string>("Path");
        _deletedOut = AddOutput<bool>("Deleted");

        AddRunGate();
    }

    /// <summary>Inline path, used when the Path input is not wired.</summary>
    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    /// <summary>Allow deleting a directory that is not empty, along with everything in it.</summary>
    public bool Recursive
    {
        get => _recursive;
        set => Set(ref _recursive, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        _deletedOut.Value = false;

        var path = _pathIn.ValueOr(Path).Trim().Trim('"');
        if (path.Length == 0)
            throw new InvalidOperationException("No path set.");

        path = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

        // Reports whether this pass actually removed something: File.Delete is silent about a file
        // that was not there, and "nothing to do" should not read as success.
        if (File.Exists(path))
        {
            File.Delete(path);
            _deletedOut.Value = true;
            return;
        }

        if (!Directory.Exists(path))
            return;

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(path, recursive: false);
            _deletedOut.Value = true;
            return;
        }

        // GetFullPath normalises away a trailing separator, so "C:\" and "C:" both compare equal here.
        if (string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(path),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetPathRoot(path) ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete the drive root {path}.");
        }

        var recursive = Recursive;
        if (!recursive && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The directory is not empty. Tick Recursive to delete it with its contents.");

        // A large tree takes a while; keep it off the evaluation path.
        var token = context.CancellationToken;
        await Task.Run(() => DeleteTree(path, token), token).ConfigureAwait(false);
        _deletedOut.Value = true;
    }

    private static void DeleteTree(string path, CancellationToken token)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();

            if (entry is DirectoryInfo directory && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                DeleteTree(directory.FullName, token);
            else if (entry is DirectoryInfo link)
                link.Delete(recursive: false); // The link itself, not what it points at.
            else
                entry.Delete();
        }

        Directory.Delete(path, recursive: false);
    }
}
