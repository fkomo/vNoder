using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Input/Static Text", Description = "A fixed string, edited in place.", Resizable = true)]
public sealed class StaticTextNode : NodeBase
{
    private string _text = string.Empty;
    private readonly OutputPort _out;

    public StaticTextNode() => _out = AddOutput<string>("Text");

    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = Text;
        return Task.CompletedTask;
    }
}
