using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Terminal display node. Multiple instances are allowed anywhere in the graph; each shows whatever
/// reaches it, formatted by type.
/// </summary>
/// <remarks>
/// Shows the value in full and nothing but the value: no truncation, byte counts, glyphs or
/// placeholder when empty, since it is meant to be read and copied verbatim. Sizes are
/// <see cref="LengthNode"/>'s job; a long value is read by resizing the node or ticking Wrap.
/// </remarks>
[NodeDefinition("Output/Output", Merges = true, Description = "Shows the value wired into it.", Resizable = true)]
public sealed class OutputNode : NodeBase
{
    private bool _wrap;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public OutputNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);

        // Passes its input straight through, so a display can sit mid-chain rather than only at the end.
        _out = AddOutput("Value", PortTypeCompatibility.Any);
    }

    /// <summary>Whether the preview wraps long values instead of clipping them.</summary>
    public bool Wrap
    {
        get => _wrap;
        set => Set(ref _wrap, value);
    }

    /// <summary>
    /// Last value seen. Assigned during evaluation without notifying: the view is refreshed on the UI
    /// thread once the pass completes, via <see cref="NodeBase.RaiseBindingsChanged"/>.
    /// </summary>
    public object? Value { get; private set; }

    public string DisplayText => NodeValues.ToText(Value);

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Value = _in.Value;
        _out.Value = _in.Value;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Run per item (its input ticked "For each item"), it shows every item it passed on, as the list
    /// its output now carries, rather than whichever item came last.
    /// </summary>
    protected override void ItemsGathered() => Value = _out.Value;
}
