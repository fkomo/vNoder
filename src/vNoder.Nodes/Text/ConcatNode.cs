using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Text/Concat", Description = "Joins two strings end to end.")]
public sealed class ConcatNode : NodeBase
{
    private readonly InputPort _a;
    private readonly InputPort _b;
    private readonly OutputPort _out;

    public ConcatNode()
    {
        _a = AddInput<string>("A");
        _b = AddInput<string>("B");
        _out = AddOutput<string>("Text");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _a.ValueOr(string.Empty) + _b.ValueOr(string.Empty);
        return Task.CompletedTask;
    }
}
