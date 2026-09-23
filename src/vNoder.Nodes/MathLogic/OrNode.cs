using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Logic/Or", Description = "A OR B.")]
public sealed class OrNode : NodeBase
{
    private readonly InputPort _a;
    private readonly InputPort _b;
    private readonly OutputPort _out;

    public OrNode()
    {
        _a = AddInput<bool>("A");
        _b = AddInput<bool>("B");
        _out = AddOutput<bool>("Result");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _a.ValueOr(false) || _b.ValueOr(false);
        return Task.CompletedTask;
    }
}
