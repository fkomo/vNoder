using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Text/Count Occurrences", Description = "How many times one string appears in another.")]
public sealed class CountOccurrencesNode : NodeBase
{
    private string _find = string.Empty;

    private readonly InputPort _textIn;
    private readonly InputPort _findIn;
    private readonly OutputPort _out;

    public CountOccurrencesNode()
    {
        _textIn = AddInput<string>("Text");
        _findIn = AddInput<string>("Find");
        _out = AddOutput<double>("Count");
    }

    public string Find
    {
        get => _find;
        set => Set(ref _find, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _textIn.ValueOr(string.Empty);
        var find = _findIn.ValueOr(Find);

        if (find.Length == 0)
        {
            _out.Value = 0d;
            return Task.CompletedTask;
        }

        // Non-overlapping, which is what "how many times does this appear" usually means.
        var count = 0;
        var at = text.IndexOf(find, StringComparison.Ordinal);

        while (at >= 0)
        {
            count++;
            at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal);
        }

        _out.Value = (double)count;
        return Task.CompletedTask;
    }
}
