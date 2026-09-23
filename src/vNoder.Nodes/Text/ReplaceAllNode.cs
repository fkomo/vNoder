using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Text/Replace All", Description = "Replaces every occurrence of one string with another.")]
public sealed class ReplaceAllNode : NodeBase
{
    private string _find = string.Empty;
    private string _replaceWith = string.Empty;

    private readonly InputPort _textIn;
    private readonly InputPort _findIn;
    private readonly InputPort _replaceIn;
    private readonly OutputPort _out;

    public ReplaceAllNode()
    {
        _textIn = AddInput<string>("Text");
        _findIn = AddInput<string>("Find");
        _replaceIn = AddInput<string>("Replace");
        _out = AddOutput<string>("Text");
    }

    public string Find
    {
        get => _find;
        set => Set(ref _find, value ?? string.Empty);
    }

    public string ReplaceWith
    {
        get => _replaceWith;
        set => Set(ref _replaceWith, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _textIn.ValueOr(string.Empty);
        var find = _findIn.ValueOr(Find);

        // Replacing the empty string is an error in the framework; passing the text through is the
        // sane reading of "replace nothing".
        _out.Value = find.Length == 0 ? text : text.Replace(find, _replaceIn.ValueOr(ReplaceWith));
        return Task.CompletedTask;
    }
}
