using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Math/Add", Description = "A + B.")]
public sealed class AddNode : NodeBase
{
    private readonly InputPort _a;
    private readonly InputPort _b;
    private readonly OutputPort _out;

    public AddNode()
    {
        _a = AddInput<double>("A");
        _b = AddInput<double>("B");
        _out = AddOutput<double>("Sum");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _a.ValueOr(0d) + _b.ValueOr(0d);
        return Task.CompletedTask;
    }
}
