using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Terminal display node. Multiple instances are allowed anywhere in the graph; each shows whatever
/// reaches it, formatted by type.
/// </summary>
[NodeDefinition("Output/Output", Description = "Shows the value wired into it.", Resizable = true)]
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

    public string DisplayText => Format(Value);

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Value = _in.Value;
        _out.Value = _in.Value;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The value and nothing else: no glyphs, no byte counts, no placeholder. Anything annotating the
    /// value would have to be selected around when copying it out, and <c>LengthNode</c> already
    /// reports sizes.
    /// </summary>
    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes).ToLowerInvariant(),
        string[] parts => string.Join(Environment.NewLine, parts),
        string text => text,
        var other => other.ToString() ?? string.Empty,
    };
}
