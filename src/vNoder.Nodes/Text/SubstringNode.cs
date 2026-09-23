using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Text/Substring", Description = "A slice of the input text.")]
public sealed class SubstringNode : NodeBase
{
    private double _start;
    private double _length = 10;

    private readonly InputPort _textIn;
    private readonly InputPort _startIn;
    private readonly InputPort _lengthIn;
    private readonly OutputPort _out;

    public SubstringNode()
    {
        _textIn = AddInput<string>("Text");
        _startIn = AddInput<double>("Start");
        _lengthIn = AddInput<double>("Length");
        _out = AddOutput<string>("Text");
    }

    public double Start
    {
        get => _start;
        set => Set(ref _start, value);
    }

    public double Length
    {
        get => _length;
        set => Set(ref _length, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _textIn.ValueOr(string.Empty);

        // Clamped rather than thrown: these are live values, and a graph running continuously would
        // otherwise spend most of its time in an error state while a bound is being edited.
        var start = (int)Math.Clamp(_startIn.ValueOr(Start), 0, text.Length);
        var length = (int)Math.Clamp(_lengthIn.ValueOr(Length), 0, text.Length - start);

        _out.Value = text.Substring(start, length);
        return Task.CompletedTask;
    }
}
