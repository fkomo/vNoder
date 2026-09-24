using vNoder.Core;

namespace vNoder.Nodes;

// Choosing between values. Both branches are always evaluated — the executor runs every node on each
// pass — so Select picks a result, it does not skip the side effects of the branch not taken.

[NodeDefinition("Logic/Select", Description = "If / else: passes on If True or If False depending on the condition.")]
public sealed class SelectNode : NodeBase
{
    private bool _condition;

    private readonly InputPort _conditionIn;
    private readonly InputPort _trueIn;
    private readonly InputPort _falseIn;
    private readonly OutputPort _out;

    public SelectNode()
    {
        _conditionIn = AddInput<bool>("Condition");
        _trueIn = AddInput("If True", PortTypeCompatibility.Any);
        _falseIn = AddInput("If False", PortTypeCompatibility.Any);
        _out = AddOutput("Result", PortTypeCompatibility.Any);
    }

    /// <summary>Inline condition, used when the Condition input is not wired.</summary>
    public bool Condition
    {
        get => _condition;
        set => Set(ref _condition, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _conditionIn.ValueOr(Condition) ? _trueIn.Value : _falseIn.Value;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Logic/Not", Description = "Inverts a boolean.")]
public sealed class NotNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public NotNode()
    {
        _in = AddInput<bool>("Value");
        _out = AddOutput<bool>("Result");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = !_in.ValueOr(false);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Logic/Is Empty", Description = "True when the value is missing, empty text, an empty list or empty bytes.")]
public sealed class IsEmptyNode : NodeBase
{
    private bool _whitespaceIsEmpty = true;

    private readonly InputPort _in;
    private readonly OutputPort _emptyOut;
    private readonly OutputPort _hasValueOut;

    public IsEmptyNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _emptyOut = AddOutput<bool>("Empty");
        _hasValueOut = AddOutput<bool>("Has Value");
    }

    /// <summary>Text of only spaces and line breaks counts as empty.</summary>
    public bool WhitespaceIsEmpty
    {
        get => _whitespaceIsEmpty;
        set => Set(ref _whitespaceIsEmpty, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var empty = NodeValues.IsEmpty(_in.Value, WhitespaceIsEmpty);
        _emptyOut.Value = empty;
        _hasValueOut.Value = !empty;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Logic/Coalesce", Description = "The first of its inputs that is not empty — a fallback chain.")]
public sealed class CoalesceNode : NodeBase
{
    private bool _whitespaceIsEmpty;

    private readonly InputPort[] _ins;
    private readonly OutputPort _out;

    public CoalesceNode()
    {
        _ins =
        [
            AddInput("A", PortTypeCompatibility.Any),
            AddInput("B", PortTypeCompatibility.Any),
            AddInput("C", PortTypeCompatibility.Any),
            AddInput("D", PortTypeCompatibility.Any),
        ];
        _out = AddOutput("Result", PortTypeCompatibility.Any);
    }

    /// <summary>Skip text of only spaces and line breaks, as well as empty text.</summary>
    public bool WhitespaceIsEmpty
    {
        get => _whitespaceIsEmpty;
        set => Set(ref _whitespaceIsEmpty, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _ins.Select(i => i.Value).FirstOrDefault(v => !NodeValues.IsEmpty(v, WhitespaceIsEmpty));
        return Task.CompletedTask;
    }
}
