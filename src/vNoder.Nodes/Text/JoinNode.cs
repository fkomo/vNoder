using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Text/Join", Description = "Joins a list back into one string.")]
public sealed class JoinNode : NodeBase
{
    private string _separator = ",";

    private readonly InputPort _partsIn;
    private readonly InputPort _separatorIn;
    private readonly OutputPort _out;

    public JoinNode()
    {
        _partsIn = AddInput<string[]>("Parts");
        _separatorIn = AddInput<string>("Separator");
        _out = AddOutput<string>("Text");
    }

    public string Separator
    {
        get => _separator;
        set => Set(ref _separator, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var parts = _partsIn.ValueOr(Array.Empty<string>());
        _out.Value = string.Join(_separatorIn.ValueOr(Separator), parts);
        return Task.CompletedTask;
    }
}
