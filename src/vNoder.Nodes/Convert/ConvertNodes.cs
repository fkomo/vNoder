using System.Globalization;
using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

// Type conversions. A value that cannot be converted is a result, not an error: the output falls back
// and Success reads false, so a parse that fails mid-edit, or on one line of a feed, can drive a
// Select rather than leave the node in an error state.

[NodeDefinition("Convert/To Number", Description = "Parses text (or reads a boolean or date) as a number, with a fallback.")]
public sealed class ToNumberNode : NodeBase
{
    private double _fallback;
    private string _culture = string.Empty;

    private readonly InputPort _in;
    private readonly InputPort _fallbackIn;
    private readonly OutputPort _out;
    private readonly OutputPort _successOut;

    public ToNumberNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _fallbackIn = AddInput<double>("Fallback");
        _out = AddOutput<double>("Number");
        _successOut = AddOutput<bool>("Success");
    }

    /// <summary>What the output reads when the value is not a number.</summary>
    public double Fallback
    {
        get => _fallback;
        set => Set(ref _fallback, value);
    }

    /// <summary>Culture for the decimal separator, e.g. "de-DE" for 1,5; empty means invariant (1.5).</summary>
    public string Culture
    {
        get => _culture;
        set => Set(ref _culture, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        double? number = _in.Value switch
        {
            double d => d,
            bool flag => flag ? 1 : 0,
            DateTime moment => (moment.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds,
            string text => Parse(text, NodeValues.Culture(Culture)),
            _ => null,
        };

        _out.Value = number ?? _fallbackIn.ValueOr(Fallback);
        _successOut.Value = number is not null;
        return Task.CompletedTask;
    }

    private static double? Parse(string text, CultureInfo culture)
    {
        // No thousands separators: invariant would read "1,5" as 15, which is worse than refusing it.
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, culture, out var value))
            return value;

        // Hex, as written in code: 0xFF.
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
            return hex;

        return null;
    }
}

/// <summary>How <see cref="ToTextNode"/> turns bytes into text.</summary>
public enum BytesAsText
{
    /// <summary>Decode as UTF-8 text.</summary>
    Utf8,

    /// <summary>Lower-case hex, as Output shows them.</summary>
    Hex,

    Base64,
}

[NodeDefinition("Convert/To Text", Description = "Any value as text: numbers, booleans, dates (ISO 8601), lists (one per line), bytes.")]
public sealed class ToTextNode : NodeBase
{
    private BytesAsText _bytes;
    private string _listSeparator = string.Empty;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public ToTextNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _out = AddOutput<string>("Text");
    }

    public BytesAsText Bytes
    {
        get => _bytes;
        set => Set(ref _bytes, value);
    }

    /// <summary>Between list items; empty means a line break, as Output shows a list.</summary>
    public string ListSeparator
    {
        get => _listSeparator;
        set => Set(ref _listSeparator, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = _in.Value switch
        {
            byte[] bytes => Bytes switch
            {
                BytesAsText.Hex => Convert.ToHexString(bytes).ToLowerInvariant(),
                BytesAsText.Base64 => Convert.ToBase64String(bytes),
                _ => Encoding.UTF8.GetString(bytes),
            },
            string[] parts => string.Join(ListSeparator.Length == 0 ? Environment.NewLine : ListSeparator, parts),
            var other => NodeValues.ToText(other),
        };

        return Task.CompletedTask;
    }
}

[NodeDefinition("Convert/To Boolean", Description = "Reads true/false, yes/no, on/off, 1/0 or a non-zero number as a boolean.")]
public sealed class ToBooleanNode : NodeBase
{
    private bool _fallback;

    private readonly InputPort _in;
    private readonly OutputPort _out;
    private readonly OutputPort _successOut;

    public ToBooleanNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _out = AddOutput<bool>("Boolean");
        _successOut = AddOutput<bool>("Success");
    }

    /// <summary>What the output reads when the value is not recognisably true or false.</summary>
    public bool Fallback
    {
        get => _fallback;
        set => Set(ref _fallback, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        bool? result = _in.Value switch
        {
            bool flag => flag,
            double number => !double.IsNaN(number) && number != 0,
            string text => Parse(text),
            null => false,
            _ => null,
        };

        _out.Value = result ?? Fallback;
        _successOut.Value = result is not null;
        return Task.CompletedTask;
    }

    private static bool? Parse(string text) => text.Trim().ToLowerInvariant() switch
    {
        "" or "false" or "no" or "n" or "off" or "0" => false,
        "true" or "yes" or "y" or "on" or "1" => true,
        var other when double.TryParse(other, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => !double.IsNaN(n) && n != 0,
        _ => null,
    };
}
