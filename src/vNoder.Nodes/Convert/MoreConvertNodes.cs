using System.Globalization;
using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Styles offered by <see cref="NumberFormatNode"/>. Saved by number: append, never reorder.</summary>
public enum NumberStyle
{
    /// <summary>1234.50</summary>
    Fixed,

    /// <summary>1,234.50, with the culture's group separator.</summary>
    Grouped,

    /// <summary>12.5 % from 0.125.</summary>
    Percent,

    /// <summary>1.23E+003</summary>
    Scientific,

    /// <summary>1.4 MB from 1468006 (1024-based, as Windows shows sizes).</summary>
    FileSize,

    /// <summary>A .NET format string, e.g. 0.00 or #,##0 "pcs".</summary>
    Custom,
}

[NodeDefinition("Convert/Number Format", Description = "Formats a number as text: fixed decimals, thousands separators, percent, scientific, file size (1.4 MB) or a custom format.")]
public sealed class NumberFormatNode : NodeBase
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    private NumberStyle _style = NumberStyle.Grouped;
    private double _decimals = 2;
    private string _culture = string.Empty;
    private string _format = "0.00";

    private readonly InputPort _in;
    private readonly InputPort _decimalsIn;
    private readonly OutputPort _out;

    public NumberFormatNode()
    {
        _in = AddInput<double>("Number");
        _decimalsIn = AddInput<double>("Decimals");
        _out = AddOutput<string>("Text");
    }

    public NumberStyle Style { get => _style; set => Set(ref _style, value); }

    public double Decimals { get => _decimals; set => Set(ref _decimals, value); }

    /// <summary>Culture for separators, e.g. "de-DE" for 1.234,50; empty means invariant.</summary>
    public string Culture { get => _culture; set => Set(ref _culture, value ?? string.Empty); }

    /// <summary>The format string used by the Custom style.</summary>
    public string Format { get => _format; set => Set(ref _format, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var number = _in.ValueOr(0d);
        var decimalsValue = _decimalsIn.ValueOr(Decimals);
        var decimals = double.IsNaN(decimalsValue) ? 0 : (int)Math.Clamp(decimalsValue, 0, 15);
        var culture = NodeValues.Culture(Culture);

        _out.Value = Style switch
        {
            NumberStyle.Fixed => number.ToString("F" + decimals, culture),
            NumberStyle.Percent => number.ToString("P" + decimals, culture),
            NumberStyle.Scientific => number.ToString("E" + decimals, culture),
            NumberStyle.FileSize => FileSize(number, decimals, culture),
            NumberStyle.Custom => number.ToString(Format, culture),
            _ => number.ToString("N" + decimals, culture),
        };

        return Task.CompletedTask;
    }

    private static string FileSize(double bytes, int decimals, CultureInfo culture)
    {
        if (!double.IsFinite(bytes))
            return bytes.ToString(culture);

        var unit = 0;
        var size = Math.Abs(bytes);
        while (size >= 1024 && unit < SizeUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Whole bytes have no fraction to show.
        var digits = unit == 0 ? 0 : decimals;
        return $"{(bytes < 0 ? "-" : string.Empty)}{size.ToString("0." + new string('#', digits), culture)} {SizeUnits[unit]}";
    }
}

/// <summary>
/// Converts whole numbers between bases 2 to 36: 255 to "ff", "1010" in base 2 to 10. Works on 64-bit
/// integers, so text beyond ±9.2e18 is refused; a number is truncated to a whole number first.
/// </summary>
[NodeDefinition("Convert/Base Convert", Description = "Converts whole numbers between bases: decimal, hexadecimal, binary, octal or any base 2–36.")]
public sealed class BaseConvertNode : NodeBase
{
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

    private double _fromBase = 10;
    private double _toBase = 16;
    private bool _uppercase;

    private readonly InputPort _in;
    private readonly InputPort _fromBaseIn;
    private readonly InputPort _toBaseIn;
    private readonly OutputPort _textOut;
    private readonly OutputPort _numberOut;

    public BaseConvertNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _fromBaseIn = AddInput<double>("From Base");
        _toBaseIn = AddInput<double>("To Base");
        _textOut = AddOutput<string>("Text");
        _numberOut = AddOutput<double>("Number");
    }

    /// <summary>The base text input is read in; a number input is always a number, whatever this says.</summary>
    public double FromBase { get => _fromBase; set => Set(ref _fromBase, value); }

    public double ToBase { get => _toBase; set => Set(ref _toBase, value); }

    public bool Uppercase { get => _uppercase; set => Set(ref _uppercase, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var from = Base(_fromBaseIn.ValueOr(FromBase));
        var to = Base(_toBaseIn.ValueOr(ToBase));

        var value = _in.Value switch
        {
            null => 0L,
            double number when double.IsFinite(number) => (long)Math.Clamp(Math.Truncate(number), long.MinValue, long.MaxValue),
            double => throw new InvalidOperationException("Infinity and NaN have no digits."),
            string text => Parse(text, from),
            var other => Parse(NodeValues.ToText(other), from),
        };

        var digits = Format(value, to);
        _textOut.Value = Uppercase ? digits.ToUpperInvariant() : digits;
        _numberOut.Value = (double)value;
        return Task.CompletedTask;
    }

    private static int Base(double value) =>
        double.IsNaN(value) ? 10 : (int)Math.Clamp(value, 2, 36);

    private static long Parse(string text, int radix)
    {
        var span = text.Trim().Replace("_", string.Empty).Replace(" ", string.Empty).AsSpan();
        var negative = span.StartsWith("-");
        if (negative || span.StartsWith("+"))
            span = span[1..];

        // The usual prefixes, when they agree with the base being read.
        if (span.Length > 2 && span[0] == '0')
        {
            var prefix = char.ToLowerInvariant(span[1]);
            if ((prefix == 'x' && radix == 16) || (prefix == 'b' && radix == 2) || (prefix == 'o' && radix == 8))
                span = span[2..];
        }

        if (span.Length == 0)
            throw new InvalidOperationException("No digits to convert.");

        ulong result = 0;
        foreach (var c in span)
        {
            var digit = Digits.IndexOf(char.ToLowerInvariant(c));
            if (digit < 0 || digit >= radix)
                throw new InvalidOperationException($"'{c}' is not a digit in base {radix}.");

            if (result > (ulong.MaxValue - (ulong)digit) / (ulong)radix)
                throw TooLarge();

            result = (result * (ulong)radix) + (ulong)digit;
        }

        // Negated through ulong arithmetic, so -9223372036854775808 still fits.
        return negative
            ? result <= (ulong)long.MaxValue + 1 ? (long)(0 - result) : throw TooLarge()
            : result <= long.MaxValue ? (long)result : throw TooLarge();
    }

    private static InvalidOperationException TooLarge() => new("The number is too large (over 64 bits).");

    private static string Format(long value, int radix)
    {
        if (value == 0)
            return "0";

        // Through ulong, so long.MinValue has a magnitude.
        var magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        var builder = new StringBuilder();
        while (magnitude > 0)
        {
            builder.Insert(0, Digits[(int)(magnitude % (ulong)radix)]);
            magnitude /= (ulong)radix;
        }

        return value < 0 ? "-" + builder : builder.ToString();
    }
}
