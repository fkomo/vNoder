using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Deletes a file. Destructive and unconditional: wired into a continuous run it will fire on every
/// tick, which is the user's call to make.
/// </summary>
[NodeDefinition("IO/File Delete", Description = "Deletes the file at the given path.", Resizable = true)]
public sealed class FileDeleteNode : NodeBase
{
    private string _path = string.Empty;

    private readonly InputPort _pathIn;
    private readonly OutputPort _deletedOut;

    public FileDeleteNode()
    {
        _pathIn = AddInput<string>("Path");
        _deletedOut = AddOutput<bool>("Deleted");
    }

    /// <summary>Inline path, used when the Path input is not wired.</summary>
    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _deletedOut.Value = false;

        var path = _pathIn.ValueOr(Path);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("No file path set.");

        // Reports whether this pass actually removed something: File.Delete is silent about a file
        // that was not there, and "nothing to do" should not read as success.
        if (!File.Exists(path))
            return Task.CompletedTask;

        File.Delete(path);
        _deletedOut.Value = true;
        return Task.CompletedTask;
    }
}
