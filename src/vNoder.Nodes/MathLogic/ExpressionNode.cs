using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Evaluates a formula written as text, in up to four variables: <c>sqrt(x^2 + y^2)</c>,
/// <c>2x + 1</c>, <c>if(x &lt; 0, -x, x)</c>. See <see cref="MathExpression"/> for the syntax.
/// </summary>
/// <remarks>
/// X, Y, Z and W are wirable parameters, so a formula can take computed values or fixed ones. A list
/// wired into one runs the formula per item like any node, which is how a whole curve goes to Plot.
/// The formula is parsed only when its text changes. One that does not parse throws, as an invalid
/// regex does: there is no sane value for it. An empty formula gives no value rather than an error, so
/// clearing the box while editing does not put a continuous run into an error state.
/// </remarks>
[NodeDefinition("Math/Expression", Description = "Evaluates a formula in X, Y, Z and W, e.g. sqrt(x^2 + y^2).")]
public sealed class ExpressionNode : NodeBase
{
    private string _formula = "x * 2";
    private double _x;
    private double _y;
    private double _z;
    private double _w;
    private bool _degrees;

    /// <summary>The last formula parsed, reused until the text changes. Touched only by evaluation.</summary>
    private MathExpression? _parsed;

    private readonly InputPort _xIn;
    private readonly InputPort _yIn;
    private readonly InputPort _zIn;
    private readonly InputPort _wIn;
    private readonly OutputPort _out;

    public ExpressionNode()
    {
        _xIn = AddInput<double>("X");
        _yIn = AddInput<double>("Y");
        _zIn = AddInput<double>("Z");
        _wIn = AddInput<double>("W");
        _out = AddOutput<double>("Result");
    }

    /// <summary>The formula. Not called Expression, which reads as the node type's own name.</summary>
    public string Formula
    {
        get => _formula;
        set => Set(ref _formula, value ?? string.Empty);
    }

    public double X { get => _x; set => Set(ref _x, value); }

    public double Y { get => _y; set => Set(ref _y, value); }

    public double Z { get => _z; set => Set(ref _z, value); }

    public double W { get => _w; set => Set(ref _w, value); }

    /// <summary>Angles in degrees rather than radians, as on the trig nodes.</summary>
    public bool Degrees
    {
        get => _degrees;
        set => Set(ref _degrees, value);
    }

    /// <summary>Shown under the formula box, so the syntax is discoverable without leaving the node.</summary>
    public string Hint =>
        "x y z w · + - * / % ^ · < > == · pi e · " + string.Join(" ", MathExpression.Functions);

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Read once: the property is edited on the UI thread while this runs off it.
        var formula = Formula;

        if (string.IsNullOrWhiteSpace(formula))
        {
            _out.Value = null;
            return Task.CompletedTask;
        }

        if (_parsed?.Text != formula)
            _parsed = MathExpression.Parse(formula);

        _out.Value = _parsed.Evaluate(
            _xIn.ValueOr(X),
            _yIn.ValueOr(Y),
            _zIn.ValueOr(Z),
            _wIn.ValueOr(W),
            Degrees);

        return Task.CompletedTask;
    }
}
