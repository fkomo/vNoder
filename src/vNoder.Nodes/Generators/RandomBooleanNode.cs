using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Generators/Random Boolean", Description = "True with the given probability.")]
public sealed class RandomBooleanNode : NodeBase
{
    private double _probability = 0.5;

    private readonly InputPort _probabilityIn;
    private readonly OutputPort _out;

    public RandomBooleanNode()
    {
        _probabilityIn = AddInput<double>("Probability");
        _out = AddOutput<bool>("Value");
    }

    /// <summary>Chance of true, from 0 to 1.</summary>
    public double Probability
    {
        get => _probability;
        set => Set(ref _probability, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var probability = Math.Clamp(_probabilityIn.ValueOr(Probability), 0, 1);
        _out.Value = Random.Shared.NextDouble() < probability;
        return Task.CompletedTask;
    }
}
