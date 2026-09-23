using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Three-way comparison: -1, 0 or 1, so it can drive arithmetic as well as be read.</summary>
[NodeDefinition("Logic/Compare", Description = "-1, 0 or 1 depending on which input is greater.")]
public sealed class CompareNode : NodeBase
{
    private bool _ignoreCase;

    private readonly InputPort _a;
    private readonly InputPort _b;
    private readonly OutputPort _out;

    public CompareNode()
    {
        _a = AddInput("A", PortTypeCompatibility.Any);
        _b = AddInput("B", PortTypeCompatibility.Any);
        _out = AddOutput<double>("Result");
    }

    /// <summary>Applies only when the comparison falls back to text.</summary>
    public bool IgnoreCase
    {
        get => _ignoreCase;
        set => Set(ref _ignoreCase, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Numbers compare numerically; everything else compares as the text it prints as.
        _out.Value = (_a.Value, _b.Value) switch
        {
            (double a, double b) => (double)a.CompareTo(b),
            (DateTime a, DateTime b) => (double)a.CompareTo(b),
            var (a, b) => (double)Math.Sign(string.Compare(
                EqualsNode.Text(a),
                EqualsNode.Text(b),
                IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)),
        };

        return Task.CompletedTask;
    }
}
