using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Logic/Boolean", Description = "A fixed true or false.")]
public sealed class BooleanNode : NodeBase
{
    private bool _value;
    private readonly OutputPort _out;

    public BooleanNode() => _out = AddOutput<bool>("Value");

    public bool Value
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
