using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Text/To Upper", Description = "Upper-cases the input text.")]
public sealed class ToUpperNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public ToUpperNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<string>("Text");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _in.ValueOr(string.Empty).ToUpper(CultureInfo.InvariantCulture);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Text/To Lower", Description = "Lower-cases the input text.")]
public sealed class ToLowerNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public ToLowerNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<string>("Text");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _in.ValueOr(string.Empty).ToLower(CultureInfo.InvariantCulture);
        return Task.CompletedTask;
    }
}
