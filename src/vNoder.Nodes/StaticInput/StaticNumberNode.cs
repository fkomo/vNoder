using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Input/Static Number", Description = "A fixed number, edited in place.")]
public sealed class StaticNumberNode : NodeBase
{
    private double _value;
    private readonly OutputPort _out;

    public StaticNumberNode() => _out = AddOutput<double>("Value");

    public double Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = Value;
        return Task.CompletedTask;
    }
}
