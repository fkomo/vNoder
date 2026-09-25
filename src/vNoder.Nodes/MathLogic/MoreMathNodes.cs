using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>What <see cref="AggregateNode"/> computes. Saved by number: append, never reorder.</summary>
public enum AggregateKind
{
    Sum,
    Average,
    Min,
    Max,
    Product,
    Median,

    /// <summary>Sample standard deviation (n − 1), the usual one for measurements.</summary>
    StdDev,
}

/// <summary>
/// One number from a list: the step that turns a per-item run back into a single result. The input is
/// "any" so a number list and a text list of numbers both fit whole; items that are not numbers are
/// left out, and Count says how many were used.
/// </summary>
[NodeDefinition("Math/Aggregate", Description = "Sum, average, min, max, product, median or standard deviation of a list of numbers.")]
public sealed class AggregateNode : NodeBase
{
    private AggregateKind _kind;

    private readonly InputPort _in;
    private readonly OutputPort _out;
    private readonly OutputPort _countOut;

    public AggregateNode()
    {
        _in = AddInput("List", PortTypeCompatibility.Any);
        _out = AddOutput<double>("Result");
        _countOut = AddOutput<double>("Count");
    }

    public AggregateKind Kind
    {
        get => _kind;
        set => Set(ref _kind, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var numbers = NodeValues.Items(_in.Value)
            .Select(NodeValues.AsNumber)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .ToArray();

        _countOut.Value = (double)numbers.Length;

        // Sum and product of nothing are well defined; the rest have no answer, so they are empty and
        // a Coalesce downstream can supply one.
        _out.Value = Kind switch
        {
            AggregateKind.Sum => numbers.Sum(),
            AggregateKind.Product => numbers.Aggregate(1d, (a, b) => a * b),
            _ when numbers.Length == 0 => null,
            AggregateKind.Average => numbers.Average(),
            AggregateKind.Min => numbers.Min(),
            AggregateKind.Max => numbers.Max(),
            AggregateKind.Median => Median(numbers),
            AggregateKind.StdDev => numbers.Length < 2 ? 0d : StdDev(numbers),
            _ => null,
        };

        return Task.CompletedTask;
    }

    private static double Median(double[] numbers)
    {
        var sorted = numbers.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double StdDev(double[] numbers)
    {
        var mean = numbers.Average();
        return Math.Sqrt(numbers.Sum(n => (n - mean) * (n - mean)) / (numbers.Length - 1));
    }
}

[NodeDefinition("Math/Map Range", Description = "Rescales a value from one range to another, e.g. 0–1023 to 0–100 %.")]
public sealed class MapRangeNode : NodeBase
{
    private double _inMin;
    private double _inMax = 1;
    private double _outMin;
    private double _outMax = 100;
    private bool _clamp;

    private readonly InputPort _valueIn;
    private readonly InputPort _inMinIn;
    private readonly InputPort _inMaxIn;
    private readonly InputPort _outMinIn;
    private readonly InputPort _outMaxIn;
    private readonly OutputPort _out;

    public MapRangeNode()
    {
        _valueIn = AddInput<double>("Value");
        _inMinIn = AddInput<double>("In Min");
        _inMaxIn = AddInput<double>("In Max");
        _outMinIn = AddInput<double>("Out Min");
        _outMaxIn = AddInput<double>("Out Max");
        _out = AddOutput<double>("Result");
    }

    public double InMin { get => _inMin; set => Set(ref _inMin, value); }

    public double InMax { get => _inMax; set => Set(ref _inMax, value); }

    public double OutMin { get => _outMin; set => Set(ref _outMin, value); }

    public double OutMax { get => _outMax; set => Set(ref _outMax, value); }

    /// <summary>Keep the result inside the output range when the value is outside the input range.</summary>
    public bool Clamp { get => _clamp; set => Set(ref _clamp, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var inMin = _inMinIn.ValueOr(InMin);
        var inMax = _inMaxIn.ValueOr(InMax);
        var outMin = _outMinIn.ValueOr(OutMin);
        var outMax = _outMaxIn.ValueOr(OutMax);

        // An empty input range maps everything to the start of the output, rather than to NaN.
        var t = inMax == inMin ? 0 : (_valueIn.ValueOr(0d) - inMin) / (inMax - inMin);
        if (Clamp)
            t = Math.Clamp(t, 0, 1);

        _out.Value = outMin + (t * (outMax - outMin));
        return Task.CompletedTask;
    }
}

[NodeDefinition("Math/Lerp", Description = "Linear interpolation: A at T = 0, B at T = 1, in between for T between.")]
public sealed class LerpNode : NodeBase
{
    private readonly InputPort _a;
    private readonly InputPort _b;
    private readonly InputPort _t;
    private readonly OutputPort _out;

    public LerpNode()
    {
        _a = AddInput<double>("A");
        _b = AddInput<double>("B");
        _t = AddInput<double>("T");
        _out = AddOutput<double>("Result");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var a = _a.ValueOr(0d);
        _out.Value = a + ((_b.ValueOr(1d) - a) * _t.ValueOr(0d));
        return Task.CompletedTask;
    }
}

[NodeDefinition("Math/Atan2", Description = "The angle of the point (X, Y) from the X axis, in the full circle.")]
public sealed class Atan2Node : NodeBase
{
    private bool _degrees;

    private readonly InputPort _y;
    private readonly InputPort _x;
    private readonly OutputPort _out;

    public Atan2Node()
    {
        _y = AddInput<double>("Y");
        _x = AddInput<double>("X");
        _out = AddOutput<double>("Angle");
    }

    public bool Degrees
    {
        get => _degrees;
        set => Set(ref _degrees, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var radians = Math.Atan2(_y.ValueOr(0d), _x.ValueOr(1d));
        _out.Value = Degrees ? radians * 180 / Math.PI : radians;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Math/Log", Description = "Logarithm of A in base B (2 when B is not wired).")]
public sealed class LogarithmNode : BinaryMathNode
{
    public LogarithmNode() : base(defaultB: 2) { }

    protected override double Apply(double a, double b) => Math.Log(a, b);
}

/// <summary>Operations offered by <see cref="BitwiseNode"/>. Saved by number: append, never reorder.</summary>
public enum BitwiseOperation
{
    And,
    Or,
    Xor,

    /// <summary>Inverts A; B is not used.</summary>
    Not,

    ShiftLeft,
    ShiftRight,
}

/// <summary>
/// Bit operations on whole numbers. Ports carry doubles, so operands are truncated to 64-bit integers
/// and the result converted back; past 2^53 a double cannot hold every integer, which is the limit
/// for exact results.
/// </summary>
[NodeDefinition("Math/Bitwise", Description = "AND, OR, XOR, NOT and shifts on whole numbers; flags and masks.")]
public sealed class BitwiseNode : NodeBase
{
    private BitwiseOperation _operation;

    private readonly InputPort _a;
    private readonly InputPort _b;
    private readonly OutputPort _out;

    public BitwiseNode()
    {
        _a = AddInput<double>("A");
        _b = AddInput<double>("B");
        _out = AddOutput<double>("Result");
    }

    public BitwiseOperation Operation
    {
        get => _operation;
        set => Set(ref _operation, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var a = Whole(_a.ValueOr(0d));
        var b = Whole(_b.ValueOr(0d));
        var shift = (int)Math.Clamp(b, 0, 63);

        _out.Value = (double)(Operation switch
        {
            BitwiseOperation.Or => a | b,
            BitwiseOperation.Xor => a ^ b,
            BitwiseOperation.Not => ~a,
            BitwiseOperation.ShiftLeft => a << shift,
            BitwiseOperation.ShiftRight => a >> shift,
            _ => a & b,
        });

        return Task.CompletedTask;
    }

    private static long Whole(double value) =>
        double.IsNaN(value) ? 0 : (long)Math.Clamp(Math.Truncate(value), long.MinValue, long.MaxValue);
}

[NodeDefinition("Logic/Type Of", Merges = true, Description = "The kind of value that arrived: number, text, boolean, bytes, date/time, a list of those, or empty.")]
public sealed class TypeOfNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;
    private readonly OutputPort _isListOut;

    public TypeOfNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _out = AddOutput<string>("Type");
        _isListOut = AddOutput<bool>("Is List");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _in.Value is { } value ? PortTypeCompatibility.Describe(value.GetType()) : "empty";
        _isListOut.Value = PortTypeCompatibility.IsListValue(_in.Value);
        return Task.CompletedTask;
    }
}
