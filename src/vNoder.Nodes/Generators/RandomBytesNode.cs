using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Generators/Random Bytes", Description = "Cryptographically random byte buffer.")]
public sealed class RandomBytesNode : NodeBase
{
    private double _length = 32;
    private readonly InputPort _lengthIn;
    private readonly OutputPort _out;

    public RandomBytesNode()
    {
        _lengthIn = AddInput<double>("Length");
        _out = AddOutput<byte[]>("Bytes");
    }

    public double Length
    {
        get => _length;
        set => Set(ref _length, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var count = (int)Math.Clamp(_lengthIn.ValueOr(Length), 0, 1 << 20);
        _out.Value = System.Security.Cryptography.RandomNumberGenerator.GetBytes(count);
        return Task.CompletedTask;
    }
}
