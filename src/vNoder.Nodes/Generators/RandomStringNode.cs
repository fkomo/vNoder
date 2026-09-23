using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Generators/Random String", Description = "Random characters drawn from a charset.")]
public sealed class RandomStringNode : NodeBase
{
    private const string DefaultCharset = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private double _length = 16;
    private string _charset = DefaultCharset;
    private readonly InputPort _lengthIn;
    private readonly InputPort _charsetIn;
    private readonly OutputPort _out;

    public RandomStringNode()
    {
        _lengthIn = AddInput<double>("Length");
        _charsetIn = AddInput<string>("Charset");
        _out = AddOutput<string>("Text");
    }

    public double Length
    {
        get => _length;
        set => Set(ref _length, value);
    }

    public string Charset
    {
        get => _charset;
        set => Set(ref _charset, string.IsNullOrEmpty(value) ? DefaultCharset : value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var count = (int)Math.Clamp(_lengthIn.ValueOr(Length), 0, 4096);
        var charset = _charsetIn.ValueOr(Charset);

        if (string.IsNullOrEmpty(charset))
            charset = Charset;
        var buffer = new char[count];

        for (var i = 0; i < count; i++)
            buffer[i] = charset[Random.Shared.Next(charset.Length)];

        _out.Value = new string(buffer);
        return Task.CompletedTask;
    }
}
