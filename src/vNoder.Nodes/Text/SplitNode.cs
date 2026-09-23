using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Text/Split", Description = "Splits text on a separator into a list.")]
public sealed class SplitNode : NodeBase
{
    private string _separator = ",";

    private readonly InputPort _textIn;
    private readonly InputPort _separatorIn;
    private readonly OutputPort _out;

    public SplitNode()
    {
        _textIn = AddInput<string>("Text");
        _separatorIn = AddInput<string>("Separator");
        _out = AddOutput<string[]>("Parts");
    }

    public string Separator
    {
        get => _separator;
        set => Set(ref _separator, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _textIn.ValueOr(string.Empty);
        var separator = _separatorIn.ValueOr(Separator);

        // Splitting on nothing yields the whole text, rather than one part per character.
        _out.Value = separator.Length == 0
            ? new[] { text }
            : text.Split(separator, StringSplitOptions.None);

        return Task.CompletedTask;
    }
}
