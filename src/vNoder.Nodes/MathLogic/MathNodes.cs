using vNoder.Core;

namespace vNoder.Nodes;

// The arithmetic basics. Each operator is still its own attributed class, as every node is; the two
// base classes only hold the port declarations, which would otherwise be repeated verbatim twenty
// times. Results follow IEEE rules rather than throwing — 1 / 0 is infinity, sqrt(-1) is NaN — so a
// graph running continuously does not sit in an error state while an operand passes through zero.

/// <summary>A node computing one number from one number.</summary>
public abstract class UnaryMathNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    protected UnaryMathNode()
    {
        _in = AddInput<double>("Value");
        _out = AddOutput<double>("Result");
    }

    protected abstract double Apply(double value);

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = Apply(_in.ValueOr(0d));
        return Task.CompletedTask;
    }
}

/// <summary>A node computing one number from two.</summary>
public abstract class BinaryMathNode : NodeBase
{
    private readonly InputPort _a;
    private readonly InputPort _b;
    private readonly OutputPort _out;

    protected BinaryMathNode(double defaultB = 0)
    {
        _a = AddInput<double>("A");
        _b = AddInput<double>("B");
        _out = AddOutput<double>("Result");
        DefaultB = defaultB;
    }

    /// <summary>What an unwired B reads as: 1 for multiply and divide, so a lone A passes through.</summary>
    private double DefaultB { get; }

    protected abstract double Apply(double a, double b);

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = Apply(_a.ValueOr(0d), _b.ValueOr(DefaultB));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Trigonometry, in degrees or radians. The angle unit is inline state on this shared base, so every
/// trig node gets the same checkbox from one editor template.
/// </summary>
public abstract class TrigNode : UnaryMathNode
{
    private bool _degrees;

    /// <summary>Angles in degrees rather than radians: inputs for sin/cos/tan, outputs for the inverses.</summary>
    public bool Degrees
    {
        get => _degrees;
        set => Set(ref _degrees, value);
    }

    protected double ToRadians(double angle) => Degrees ? angle * Math.PI / 180 : angle;

    protected double FromRadians(double angle) => Degrees ? angle * 180 / Math.PI : angle;
}

// ---- Binary ---------------------------------------------------------------------------------

[NodeDefinition("Math/Subtract", Description = "A - B.")]
public sealed class SubtractNode : BinaryMathNode
{
    protected override double Apply(double a, double b) => a - b;
}

[NodeDefinition("Math/Multiply", Description = "A × B.")]
public sealed class MultiplyNode : BinaryMathNode
{
    public MultiplyNode() : base(defaultB: 1) { }

    protected override double Apply(double a, double b) => a * b;
}

[NodeDefinition("Math/Divide", Description = "A ÷ B. Dividing by zero gives infinity, not an error.")]
public sealed class DivideNode : BinaryMathNode
{
    public DivideNode() : base(defaultB: 1) { }

    protected override double Apply(double a, double b) => a / b;
}

[NodeDefinition("Math/Modulo", Description = "Remainder of A ÷ B, with the sign of A.")]
public sealed class ModuloNode : BinaryMathNode
{
    public ModuloNode() : base(defaultB: 1) { }

    protected override double Apply(double a, double b) => a % b;
}

[NodeDefinition("Math/Power", Description = "A raised to the power B.")]
public sealed class PowerNode : BinaryMathNode
{
    public PowerNode() : base(defaultB: 1) { }

    protected override double Apply(double a, double b) => Math.Pow(a, b);
}

[NodeDefinition("Math/Min", Description = "The smaller of A and B.")]
public sealed class MinNode : BinaryMathNode
{
    protected override double Apply(double a, double b) => Math.Min(a, b);
}

[NodeDefinition("Math/Max", Description = "The larger of A and B.")]
public sealed class MaxNode : BinaryMathNode
{
    protected override double Apply(double a, double b) => Math.Max(a, b);
}

// ---- Unary ----------------------------------------------------------------------------------

[NodeDefinition("Math/Abs", Description = "Absolute value.")]
public sealed class AbsNode : UnaryMathNode
{
    protected override double Apply(double value) => Math.Abs(value);
}

[NodeDefinition("Math/Negate", Description = "The value with its sign flipped.")]
public sealed class NegateNode : UnaryMathNode
{
    protected override double Apply(double value) => -value;
}

[NodeDefinition("Math/Sign", Description = "-1, 0 or 1 by the sign of the value.")]
public sealed class SignNode : UnaryMathNode
{
    protected override double Apply(double value) => double.IsNaN(value) ? double.NaN : Math.Sign(value);
}

[NodeDefinition("Math/Floor", Description = "Rounds down to a whole number.")]
public sealed class FloorNode : UnaryMathNode
{
    protected override double Apply(double value) => Math.Floor(value);
}

[NodeDefinition("Math/Ceiling", Description = "Rounds up to a whole number.")]
public sealed class CeilingNode : UnaryMathNode
{
    protected override double Apply(double value) => Math.Ceiling(value);
}

[NodeDefinition("Math/Exp", Description = "e raised to the value.")]
public sealed class ExpNode : UnaryMathNode
{
    protected override double Apply(double value) => Math.Exp(value);
}

[NodeDefinition("Math/Ln", Description = "Natural logarithm.")]
public sealed class LnNode : UnaryMathNode
{
    protected override double Apply(double value) => Math.Log(value);
}

[NodeDefinition("Math/Log10", Description = "Base-10 logarithm.")]
public sealed class Log10Node : UnaryMathNode
{
    protected override double Apply(double value) => Math.Log10(value);
}

[NodeDefinition("Math/Sin", Description = "Sine of an angle.")]
public sealed class SinNode : TrigNode
{
    protected override double Apply(double value) => Math.Sin(ToRadians(value));
}

[NodeDefinition("Math/Cos", Description = "Cosine of an angle.")]
public sealed class CosNode : TrigNode
{
    protected override double Apply(double value) => Math.Cos(ToRadians(value));
}

[NodeDefinition("Math/Tan", Description = "Tangent of an angle.")]
public sealed class TanNode : TrigNode
{
    protected override double Apply(double value) => Math.Tan(ToRadians(value));
}

[NodeDefinition("Math/Asin", Description = "Arcsine: the angle whose sine is the value.")]
public sealed class AsinNode : TrigNode
{
    protected override double Apply(double value) => FromRadians(Math.Asin(value));
}

[NodeDefinition("Math/Acos", Description = "Arccosine: the angle whose cosine is the value.")]
public sealed class AcosNode : TrigNode
{
    protected override double Apply(double value) => FromRadians(Math.Acos(value));
}

[NodeDefinition("Math/Atan", Description = "Arctangent: the angle whose tangent is the value.")]
public sealed class AtanNode : TrigNode
{
    protected override double Apply(double value) => FromRadians(Math.Atan(value));
}

// ---- With parameters ------------------------------------------------------------------------

[NodeDefinition("Math/Round", Description = "Rounds to a number of decimal places (halves away from zero).")]
public sealed class RoundNode : NodeBase
{
    private double _digits;

    private readonly InputPort _in;
    private readonly InputPort _digitsIn;
    private readonly OutputPort _out;

    public RoundNode()
    {
        _in = AddInput<double>("Value");
        _digitsIn = AddInput<double>("Digits");
        _out = AddOutput<double>("Result");
    }

    public double Digits
    {
        get => _digits;
        set => Set(ref _digits, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Math.Round only takes 0..15 digits; clamp rather than throw, like the text nodes.
        var digits = (int)Math.Clamp(_digitsIn.ValueOr(Digits), 0, 15);
        _out.Value = Math.Round(_in.ValueOr(0d), digits, MidpointRounding.AwayFromZero);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Math/Clamp", Description = "Limits the value to the range Min..Max.")]
public sealed class ClampNode : NodeBase
{
    private double _min;
    private double _max = 1;

    private readonly InputPort _in;
    private readonly InputPort _minIn;
    private readonly InputPort _maxIn;
    private readonly OutputPort _out;

    public ClampNode()
    {
        _in = AddInput<double>("Value");
        _minIn = AddInput<double>("Min");
        _maxIn = AddInput<double>("Max");
        _out = AddOutput<double>("Result");
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
        var min = _minIn.ValueOr(Min);
        var max = _maxIn.ValueOr(Max);

        // Math.Clamp throws when the bounds cross, which happens mid-edit; take them in either order.
        _out.Value = Math.Clamp(_in.ValueOr(0d), Math.Min(min, max), Math.Max(min, max));
        return Task.CompletedTask;
    }
}

/// <summary>Constants offered by <see cref="ConstantNode"/>.</summary>
public enum MathConstant
{
    Pi,
    Tau,
    E,
}

[NodeDefinition("Math/Constant", Description = "π, τ or e.")]
public sealed class ConstantNode : NodeBase
{
    private MathConstant _constant = MathConstant.Pi;
    private readonly OutputPort _out;

    public ConstantNode() => _out = AddOutput<double>("Value");

    public MathConstant Constant
    {
        get => _constant;
        set => Set(ref _constant, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = Constant switch
        {
            MathConstant.Tau => Math.Tau,
            MathConstant.E => Math.E,
            _ => Math.PI,
        };

        return Task.CompletedTask;
    }
}
