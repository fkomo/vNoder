using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Generators/Random Number", Description = "Uniform random number in [Min, Max).")]
public sealed class RandomNumberNode : NodeBase
{
    private double _min;
    private double _max = 1;
    private readonly InputPort _minIn;
    private readonly InputPort _maxIn;
    private readonly OutputPort _out;

    public RandomNumberNode()
    {
        _minIn = AddInput<double>("Min");
        _maxIn = AddInput<double>("Max");
        _out = AddOutput<double>("Value");
    }

    public double Min
    {
        get => _min;
        set => Set(ref _min, value);
    }

    public double Max
    {
        get => _max;
        set => Set(ref _max, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // An unwired input reads as null, so ValueOr falls back to the inline value.
        var min = _minIn.ValueOr(Min);
        var max = _maxIn.ValueOr(Max);

        var (low, high) = min <= max ? (min, max) : (max, min);
        _out.Value = low + (Random.Shared.NextDouble() * (high - low));
        return Task.CompletedTask;
    }
}
