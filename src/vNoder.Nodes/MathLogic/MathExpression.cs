using System.Globalization;

namespace vNoder.Nodes;

/// <summary>
/// A parsed maths formula in the variables x, y, z and w, such as <c>sqrt(x^2 + y^2)</c>. Parsed once
/// into a delegate, then evaluated as often as needed, so a continuous run does not re-parse each tick.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than a package: the libraries that exist either read <c>^</c> as XOR (so
/// <c>x^2</c> would be silently wrong), are unmaintained, or carry a commercial licence, and this is a
/// couple of hundred lines. Syntax is ordinary written maths:
/// </para>
/// <list type="bullet">
/// <item><c>+ - * / %</c>, and <c>^</c> for power, right-associative (<c>2^3^2</c> is 512). Unary minus
/// binds looser than power, so <c>-x^2</c> is −(x²), as on paper.</item>
/// <item>A number directly before a name or a bracket multiplies: <c>2x</c>, <c>3(x + 1)</c>.</item>
/// <item>Names are case-insensitive: the variables, the constants <c>pi</c>, <c>tau</c> and <c>e</c>, and
/// the functions in <see cref="Functions"/>. Numbers use a dot; a comma separates arguments.</item>
/// <item><c>if(c, a, b)</c> is <c>a</c> when <c>c</c> is not zero, otherwise <c>b</c>; with
/// <c>&lt; &lt;= &gt; &gt;= == !=</c>, which give 1 or 0, that is enough for a piecewise formula.</item>
/// </list>
/// <para>
/// A formula that does not parse throws <see cref="FormatException"/> naming what is wrong and where
/// (1-based column), which the node shows as its error.
/// </para>
/// </remarks>
public sealed class MathExpression
{
    private static readonly string[] VariableNames = ["x", "y", "z", "w"];

    /// <summary>The variables a formula can use, in the order <see cref="Evaluate"/> takes them.</summary>
    public static IReadOnlyList<string> Variables => VariableNames;

    private readonly Func<Scope, double> _evaluate;

    private MathExpression(string text, Func<Scope, double> evaluate)
    {
        Text = text;
        _evaluate = evaluate;
    }

    public string Text { get; }

    /// <summary>What a formula is evaluated with: the variable values, and the angle unit for trig.</summary>
    private sealed class Scope(double[] values, bool degrees)
    {
        public double[] Values { get; } = values;
        public bool Degrees { get; } = degrees;

        public double ToRadians(double angle) => Degrees ? angle * Math.PI / 180 : angle;

        public double FromRadians(double angle) => Degrees ? angle * 180 / Math.PI : angle;
    }

    /// <summary>Evaluates with x, y, z and w; angles in degrees when <paramref name="degrees"/> is set.</summary>
    public double Evaluate(double x, double y, double z, double w, bool degrees = false) =>
        _evaluate(new Scope([x, y, z, w], degrees));

    public static MathExpression Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var parser = new Parser(text);
        var evaluate = parser.ParseAll();
        return new MathExpression(text, evaluate);
    }

    // ---- Functions --------------------------------------------------------------------------

    /// <summary>A function: its argument count range, and its body.</summary>
    private sealed record Function(int MinArgs, int MaxArgs, Func<Scope, double[], double> Apply);

    private static Function Fixed(int count, Func<Scope, double[], double> apply) => new(count, count, apply);

    /// <summary>Every function a formula can call, by lower-case name.</summary>
    private static readonly Dictionary<string, Function> FunctionTable = new()
    {
        ["sin"] = Fixed(1, (s, a) => Math.Sin(s.ToRadians(a[0]))),
        ["cos"] = Fixed(1, (s, a) => Math.Cos(s.ToRadians(a[0]))),
        ["tan"] = Fixed(1, (s, a) => Math.Tan(s.ToRadians(a[0]))),
        ["asin"] = Fixed(1, (s, a) => s.FromRadians(Math.Asin(a[0]))),
        ["acos"] = Fixed(1, (s, a) => s.FromRadians(Math.Acos(a[0]))),
        ["atan"] = Fixed(1, (s, a) => s.FromRadians(Math.Atan(a[0]))),
        ["atan2"] = Fixed(2, (s, a) => s.FromRadians(Math.Atan2(a[0], a[1]))),
        ["sinh"] = Fixed(1, (_, a) => Math.Sinh(a[0])),
        ["cosh"] = Fixed(1, (_, a) => Math.Cosh(a[0])),
        ["tanh"] = Fixed(1, (_, a) => Math.Tanh(a[0])),
        ["sqrt"] = Fixed(1, (_, a) => Math.Sqrt(a[0])),
        ["cbrt"] = Fixed(1, (_, a) => Math.Cbrt(a[0])),
        ["abs"] = Fixed(1, (_, a) => Math.Abs(a[0])),
        ["sign"] = Fixed(1, (_, a) => double.IsNaN(a[0]) ? double.NaN : Math.Sign(a[0])),
        ["floor"] = Fixed(1, (_, a) => Math.Floor(a[0])),
        ["ceil"] = Fixed(1, (_, a) => Math.Ceiling(a[0])),
        ["trunc"] = Fixed(1, (_, a) => Math.Truncate(a[0])),
        ["round"] = new(1, 2, (_, a) => a.Length == 1
            ? Math.Round(a[0], MidpointRounding.AwayFromZero)
            : Math.Round(a[0], (int)Math.Clamp(a[1], 0, 15), MidpointRounding.AwayFromZero)),
        ["exp"] = Fixed(1, (_, a) => Math.Exp(a[0])),
        ["ln"] = Fixed(1, (_, a) => Math.Log(a[0])),
        ["log10"] = Fixed(1, (_, a) => Math.Log10(a[0])),
        ["log2"] = Fixed(1, (_, a) => Math.Log2(a[0])),
        ["log"] = new(1, 2, (_, a) => a.Length == 1 ? Math.Log10(a[0]) : Math.Log(a[0], a[1])),
        ["pow"] = Fixed(2, (_, a) => Math.Pow(a[0], a[1])),
        ["hypot"] = new(2, int.MaxValue, (_, a) => Math.Sqrt(a.Sum(v => v * v))),
        ["min"] = new(1, int.MaxValue, (_, a) => a.Min()),
        ["max"] = new(1, int.MaxValue, (_, a) => a.Max()),
        ["clamp"] = Fixed(3, (_, a) => a[1] <= a[2] ? Math.Clamp(a[0], a[1], a[2]) : Math.Clamp(a[0], a[2], a[1])),
        ["lerp"] = Fixed(3, (_, a) => a[0] + ((a[1] - a[0]) * a[2])),
        ["if"] = Fixed(3, (_, a) => a[0] != 0 && !double.IsNaN(a[0]) ? a[1] : a[2]),
    };

    /// <summary>The function names, for the editor's hint and for error messages.</summary>
    public static IReadOnlyCollection<string> Functions => FunctionTable.Keys;

    private static readonly Dictionary<string, double> Constants = new()
    {
        ["pi"] = Math.PI,
        ["tau"] = Math.Tau,
        ["e"] = Math.E,
    };

    // ---- Parser -----------------------------------------------------------------------------

    /// <summary>
    /// Recursive descent, one method per precedence level, loosest first:
    /// comparison → sum → product → unary → power → primary.
    /// </summary>
    private sealed class Parser(string text)
    {
        private int _pos;

        public Func<Scope, double> ParseAll()
        {
            SkipSpace();
            if (_pos == text.Length)
                throw Error("The formula is empty");

            var result = ParseComparison();
            SkipSpace();

            if (_pos < text.Length)
            {
                // "x y" or "x(1)": two operands side by side. Only a number multiplies implicitly.
                throw char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] is '(' or '.'
                    ? Error($"Missing operator before '{text[_pos]}'")
                    : Error($"Unexpected '{text[_pos]}'");
            }

            return result;
        }

        private Func<Scope, double> ParseComparison()
        {
            var left = ParseSum();

            while (true)
            {
                SkipSpace();
                var op = PeekOperator("<=", ">=", "==", "!=", "<", ">");
                if (op is null)
                    return left;

                _pos += op.Length;
                var l = left;
                var r = ParseSum();
                left = op switch
                {
                    "<=" => s => l(s) <= r(s) ? 1 : 0,
                    ">=" => s => l(s) >= r(s) ? 1 : 0,
                    "==" => s => l(s) == r(s) ? 1 : 0,
                    "!=" => s => l(s) != r(s) ? 1 : 0,
                    "<" => s => l(s) < r(s) ? 1 : 0,
                    _ => s => l(s) > r(s) ? 1 : 0,
                };
            }
        }

        private Func<Scope, double> ParseSum()
        {
            var left = ParseProduct();

            while (true)
            {
                SkipSpace();
                if (!TryRead('+', '-', out var op))
                    return left;

                var l = left;
                var r = ParseProduct();
                left = op == '+' ? s => l(s) + r(s) : s => l(s) - r(s);
            }
        }

        private Func<Scope, double> ParseProduct()
        {
            var left = ParseUnary();

            while (true)
            {
                SkipSpace();
                if (!TryRead('*', '/', '%', out var op))
                    return left;

                var l = left;
                var r = ParseUnary();
                left = op switch
                {
                    '*' => s => l(s) * r(s),
                    '/' => s => l(s) / r(s),
                    _ => s => l(s) % r(s),
                };
            }
        }

        /// <summary>Unary signs sit above power, so -x^2 is -(x^2), and 2^-1 still works.</summary>
        private Func<Scope, double> ParseUnary()
        {
            SkipSpace();
            if (TryRead('-', '+', out var sign))
            {
                var operand = ParseUnary();
                return sign == '-' ? s => -operand(s) : operand;
            }

            return ParsePower();
        }

        /// <summary>Right-associative: the exponent is parsed as a whole unary, which may hold another power.</summary>
        private Func<Scope, double> ParsePower()
        {
            var baseValue = ParseImplicitProduct();

            SkipSpace();
            if (_pos >= text.Length || text[_pos] != '^')
                return baseValue;

            _pos++;

            var exponent = ParseUnary();
            return s => Math.Pow(baseValue(s), exponent(s));
        }

        /// <summary>A number followed directly by a name or a bracket multiplies it: 2x, 3(x + 1).</summary>
        private Func<Scope, double> ParseImplicitProduct()
        {
            SkipSpace();
            var start = _pos;
            var left = ParsePrimary();

            if (!char.IsAsciiDigit(text[start]) && text[start] != '.')
                return left;

            SkipSpace();
            if (_pos < text.Length && (char.IsAsciiLetter(text[_pos]) || text[_pos] == '('))
            {
                var right = ParsePower();
                return s => left(s) * right(s);
            }

            return left;
        }

        private Func<Scope, double> ParsePrimary()
        {
            SkipSpace();
            if (_pos == text.Length)
                throw Error("The formula ends too early");

            var c = text[_pos];

            if (c == '(')
            {
                _pos++;
                var inner = ParseComparison();
                Expect(')');
                return inner;
            }

            if (char.IsAsciiDigit(c) || c == '.')
                return ParseNumber();

            if (char.IsAsciiLetter(c) || c == '_')
                return ParseName();

            throw Error($"Unexpected '{c}'");
        }

        private Func<Scope, double> ParseNumber()
        {
            var start = _pos;

            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] == '.'))
                _pos++;

            // An exponent only when digits follow, so "2e" stays 2 times e.
            if (_pos < text.Length && text[_pos] is 'e' or 'E')
            {
                var mark = _pos + 1;
                if (mark < text.Length && text[mark] is '+' or '-')
                    mark++;

                if (mark < text.Length && char.IsAsciiDigit(text[mark]))
                {
                    _pos = mark;
                    while (_pos < text.Length && char.IsAsciiDigit(text[_pos]))
                        _pos++;
                }
            }

            var literal = text[start.._pos];
            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw Error($"'{literal}' is not a number", start);

            return _ => value;
        }

        private Func<Scope, double> ParseName()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_'))
                _pos++;

            var name = text[start.._pos].ToLowerInvariant();

            SkipSpace();
            if (_pos < text.Length && text[_pos] == '(')
                return ParseCall(name, start);

            var index = Array.IndexOf(VariableNames, name);
            if (index >= 0)
                return s => s.Values[index];

            if (Constants.TryGetValue(name, out var constant))
                return _ => constant;

            if (FunctionTable.ContainsKey(name))
                throw Error($"'{name}' is a function; call it as {name}(…)", start);

            throw Error($"Unknown name '{text[start..(start + name.Length)]}' (use x, y, z, w, pi, tau, e or a function)", start);
        }

        private Func<Scope, double> ParseCall(string name, int start)
        {
            if (!FunctionTable.TryGetValue(name, out var function))
                throw Error($"Unknown function '{name}'", start);

            _pos++; // (
            var args = new List<Func<Scope, double>>();

            SkipSpace();
            if (_pos < text.Length && text[_pos] == ')')
            {
                _pos++;
            }
            else
            {
                while (true)
                {
                    args.Add(ParseComparison());
                    SkipSpace();

                    if (_pos < text.Length && text[_pos] == ',')
                    {
                        _pos++;
                        continue;
                    }

                    Expect(')');
                    break;
                }
            }

            if (args.Count < function.MinArgs || args.Count > function.MaxArgs)
            {
                var wanted = function.MinArgs == function.MaxArgs ? $"{function.MinArgs}"
                    : function.MaxArgs == int.MaxValue ? $"at least {function.MinArgs}"
                    : $"{function.MinArgs} or {function.MaxArgs}";
                var plural = function.MaxArgs == 1 || (function.MinArgs == 1 && function.MaxArgs == int.MaxValue) ? "" : "s";
                throw Error($"{name} takes {wanted} argument{plural}, not {args.Count}", start);
            }

            var parts = args.ToArray();
            return s =>
            {
                var values = new double[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                    values[i] = parts[i](s);

                return function.Apply(s, values);
            };
        }

        // ---- Lexing helpers ----------------------------------------------------------------

        private void SkipSpace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }

        private bool TryRead(char a, char b, out char read) => TryRead([a, b], out read);

        private bool TryRead(char a, char b, char c, out char read) => TryRead([a, b, c], out read);

        private bool TryRead(ReadOnlySpan<char> options, out char read)
        {
            read = default;

            if (_pos >= text.Length || !options.Contains(text[_pos]))
                return false;

            read = text[_pos++];
            return true;
        }

        private string? PeekOperator(params string[] operators)
        {
            foreach (var op in operators)
            {
                if (string.CompareOrdinal(text, _pos, op, 0, op.Length) == 0)
                    return op;
            }

            return null;
        }

        private void Expect(char c)
        {
            SkipSpace();
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return;
            }

            throw Error(_pos < text.Length ? $"Expected '{c}' but found '{text[_pos]}'" : $"Expected '{c}' at the end");
        }

        private FormatException Error(string message, int? at = null) =>
            new($"{message} (column {(at ?? _pos) + 1}).");
    }
}
