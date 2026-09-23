using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Math/Sqrt", Description = "Square root of the input.")]
public sealed class SqrtNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public SqrtNode()
    {
        _in = AddInput<double>("Value");
        _out = AddOutput<double>("Root");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var value = _in.ValueOr(0d);
        if (value < 0)
            throw new InvalidOperationException("Square root of a negative number.");

        _out.Value = Math.Sqrt(value);
        return Task.CompletedTask;
    }
}
