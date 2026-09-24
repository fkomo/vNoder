using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace vNoder.Core;

/// <summary>
/// Base class for every node. A concrete node declares its ports in its constructor, exposes plain
/// settable properties for any user-editable state (the view binds an inline control to them), and
/// overrides <see cref="EvaluateAsync"/>.
/// </summary>
public abstract class NodeBase : INotifyPropertyChanged
{
    private readonly List<InputPort> _inputs = [];
    private readonly List<OutputPort> _outputs = [];
    private string? _title;

    /// <summary>
    /// Stable identity. Settable within this assembly only, so <see cref="GraphRestore"/> can keep the
    /// ids a saved file already uses.
    /// </summary>
    public Guid Id { get; internal set; } = Guid.NewGuid();

    public GraphPoint Position { get; set; } = GraphPoint.Origin;

    /// <summary>
    /// User-set size. <see cref="GraphSize.Auto"/> until the node is resized, which leaves the view
    /// free to decide default metrics.
    /// </summary>
    public GraphSize Size { get; set; } = GraphSize.Auto;

    public IReadOnlyList<InputPort> Inputs => _inputs;
    public IReadOnlyList<OutputPort> Outputs => _outputs;

    /// <summary>Display name, taken from <see cref="NodeDefinitionAttribute"/> when present.</summary>
    public string Title => _title ??= NodeDefinitionAttribute.For(GetType()) is { } definition
        ? definition.Path[(definition.Path.LastIndexOf('/') + 1)..]
        : GetType().Name;

    public string Category
    {
        get
        {
            var path = NodeDefinitionAttribute.For(GetType())?.Path;
            var separator = path?.LastIndexOf('/') ?? -1;
            return separator > 0 ? path![..separator] : "Misc";
        }
    }

    /// <summary>Whether the view offers a resize grip; declared on the node's own attribute.</summary>
    public bool IsResizable => NodeDefinitionAttribute.For(GetType())?.Resizable ?? false;

    /// <summary>Message from the last failed evaluation, or null. Set by <see cref="GraphExecutor"/>.</summary>
    public string? LastError { get; internal set; }

    /// <summary>
    /// Whether the last pass skipped this node: its Run If was wired and not true, or a node it takes
    /// data from failed or was skipped. Set by <see cref="GraphExecutor"/>.
    /// </summary>
    public bool IsSkipped { get; internal set; }

    /// <summary>Name of the input <see cref="AddRunGate"/> declares.</summary>
    public const string RunIfPortName = "Run If";

    /// <summary>Name of the output <see cref="AddRunGate"/> declares.</summary>
    public const string DonePortName = "Done";

    /// <summary>The Run If input, for nodes that declared one.</summary>
    internal InputPort? RunIfPort { get; private set; }

    /// <summary>The Done output, for nodes that declared one.</summary>
    internal OutputPort? DonePort { get; private set; }

    /// <summary>Declared on the attribute; see <see cref="NodeDefinitionAttribute.Merges"/>.</summary>
    internal bool Merges => _merges ??= NodeDefinitionAttribute.For(GetType())?.Merges ?? false;

    private bool? _merges;

    protected InputPort AddInput(string name, Type valueType)
    {
        var port = new InputPort(this, name, valueType, _inputs.Count);
        _inputs.Add(port);
        return port;
    }

    protected InputPort AddInput<T>(string name) => AddInput(name, typeof(T));

    protected OutputPort AddOutput(string name, Type valueType)
    {
        var port = new OutputPort(this, name, valueType, _outputs.Count);
        _outputs.Add(port);
        return port;
    }

    protected OutputPort AddOutput<T>(string name) => AddOutput(name, typeof(T));

    /// <summary>
    /// Declares the Run If input and Done output that sequence a node with side effects. Call it last
    /// in the constructor, so the two ports sit below the node's own.
    /// </summary>
    /// <remarks>
    /// The node itself does nothing with them; <see cref="GraphExecutor"/> does. An unwired Run If
    /// means "run", so a graph that never wires it behaves as if the ports were not there. Wired, the
    /// node runs only when the value is <c>true</c>, and the wire orders the two nodes like any other.
    /// Done is <c>true</c> once the node has evaluated without error: "it happened", which is not the
    /// same as a node's own Success (File Copy skipping an existing file still ran).
    /// </remarks>
    protected void AddRunGate()
    {
        RunIfPort = AddInput<bool>(RunIfPortName);
        DonePort = AddOutput<bool>(DonePortName);
    }

    /// <summary>
    /// Reads <see cref="InputPort.Value"/> on this node's inputs and writes
    /// <see cref="OutputPort.Value"/> on its outputs. Genuinely async so that I/O nodes do not block
    /// evaluation of unrelated branches.
    /// </summary>
    public abstract Task EvaluateAsync(NodeEvaluationContext context);

    /// <summary>
    /// Re-reads every binding on this node. Raised after a run completes (anything the node displays
    /// may have changed) and when its wiring changes (inline editors mirroring a port become
    /// read-only once that port is connected). Always raised on the UI thread, never from the
    /// evaluation thread.
    /// </summary>
    public void RaiseBindingsChanged() => OnPropertyChanged(string.Empty);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Setter helper for editable node state so inline controls stay in sync.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public override string ToString() => $"{Title} ({Id:D})";
}
