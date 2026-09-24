using System.Text;
using System.Text.RegularExpressions;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>How <see cref="ContainsNode"/> and <c>List/Filter</c> test text against a value.</summary>
public enum TextMatchMode
{
    Contains,
    StartsWith,
    EndsWith,
    Equals,

    /// <summary>The value is a regular expression that must match somewhere in the text.</summary>
    Regex,
}

internal static class TextMatch
{
    public static Func<string, bool> Create(string find, TextMatchMode mode, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (mode == TextMatchMode.Regex)
        {
            Regex regex;
            try
            {
                regex = new Regex(
                    find,
                    RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                    TimeSpan.FromSeconds(2)); // the same backtracking guard as RegexNode.MatchTimeout
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException($"Invalid pattern: {exception.Message}");
            }

            return text => regex.IsMatch(text);
        }

        return mode switch
        {
            TextMatchMode.StartsWith => text => text.StartsWith(find, comparison),
            TextMatchMode.EndsWith => text => text.EndsWith(find, comparison),
            TextMatchMode.Equals => text => string.Equals(text, find, comparison),
            _ => text => text.Contains(find, comparison),
        };
    }
}

[NodeDefinition("Text/Contains", Description = "True when text contains, starts with, ends with, equals or matches (regex) a value.")]
public sealed class ContainsNode : NodeBase
{
    private string _find = string.Empty;
    private TextMatchMode _mode;
    private bool _ignoreCase;

    private readonly InputPort _textIn;
    private readonly InputPort _findIn;
    private readonly OutputPort _out;

    public ContainsNode()
    {
        _textIn = AddInput<string>("Text");
        _findIn = AddInput<string>("Find");
        _out = AddOutput<bool>("Result");
    }

    public string Find
    {
        get => _find;
        set => Set(ref _find, value ?? string.Empty);
    }

    public TextMatchMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    public bool IgnoreCase
    {
        get => _ignoreCase;
        set => Set(ref _ignoreCase, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = TextMatch.Create(_findIn.ValueOr(Find), Mode, IgnoreCase)(_textIn.ValueOr(string.Empty));
        return Task.CompletedTask;
    }
}

[NodeDefinition("Text/Lines", Description = "Splits text into a list of lines, whatever the line endings.")]
public sealed class LinesNode : NodeBase
{
    private bool _trim;
    private bool _removeEmpty;

    private readonly InputPort _in;
    private readonly OutputPort _out;
    private readonly OutputPort _countOut;

    public LinesNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<string[]>("Lines");
        _countOut = AddOutput<double>("Count");
    }

    public bool Trim
    {
        get => _trim;
        set => Set(ref _trim, value);
    }

    public bool RemoveEmpty
    {
        get => _removeEmpty;
        set => Set(ref _removeEmpty, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);
        var options = (Trim ? StringSplitOptions.TrimEntries : StringSplitOptions.None)
                      | (RemoveEmpty ? StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None);

        // A trailing line break ends the last line; it does not start an empty one.
        var lines = text.Length == 0
            ? []
            : text.TrimEnd('\r', '\n').Split(["\r\n", "\n", "\r"], options);

        _out.Value = lines;
        _countOut.Value = (double)lines.Length;
        return Task.CompletedTask;
    }
}

public enum TrimSide
{
    Both,
    Start,
    End,
}

[NodeDefinition("Text/Trim", Description = "Removes whitespace, or the given characters, from the ends of text.")]
public sealed class TrimNode : NodeBase
{
    private TrimSide _side;
    private string _characters = string.Empty;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public TrimNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<string>("Text");
    }

    public TrimSide Side
    {
        get => _side;
        set => Set(ref _side, value);
    }

    /// <summary>Characters to remove; empty means whitespace.</summary>
    public string Characters
    {
        get => _characters;
        set => Set(ref _characters, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);
        var chars = Characters.Length == 0 ? null : Characters.ToCharArray();

        _out.Value = Side switch
        {
            TrimSide.Start => text.TrimStart(chars),
            TrimSide.End => text.TrimEnd(chars),
            _ => text.Trim(chars),
        };

        return Task.CompletedTask;
    }
}

public enum PadSide
{
    /// <summary>Pad on the left, right-aligning the text: " 42", "007".</summary>
    Start,

    /// <summary>Pad on the right, left-aligning the text.</summary>
    End,
}

[NodeDefinition("Text/Pad", Description = "Pads text to a width with a character, e.g. 7 to 007.")]
public sealed class PadNode : NodeBase
{
    private double _width = 8;
    private string _character = " ";
    private PadSide _side;

    private readonly InputPort _textIn;
    private readonly InputPort _widthIn;
    private readonly OutputPort _out;

    public PadNode()
    {
        // "any" so a number pads directly: 7 to 007 needs no To Text in between.
        _textIn = AddInput("Text", PortTypeCompatibility.Any);
        _widthIn = AddInput<double>("Width");
        _out = AddOutput<string>("Text");
    }

    public double Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    /// <summary>The fill character; only the first is used, and empty means a space.</summary>
    public string Character
    {
        get => _character;
        set => Set(ref _character, value ?? string.Empty);
    }

    public PadSide Side
    {
        get => _side;
        set => Set(ref _side, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = NodeValues.ToText(_textIn.Value);
        var width = _widthIn.ValueOr(Width);
        var total = double.IsNaN(width) ? 0 : (int)Math.Clamp(width, 0, 1_000_000);
        var fill = Character.Length == 0 ? ' ' : Character[0];

        // Text already wider than the width is left alone, not cut.
        _out.Value = Side == PadSide.End ? text.PadRight(total, fill) : text.PadLeft(total, fill);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Text/Template", Resizable = true, Description = "Fills {A}, {B}, {C} and {D} in a template with the wired values; other braces are kept, so JSON needs no escaping.")]
public sealed class TemplateNode : NodeBase
{
    private string _template = "{A}";

    private readonly InputPort _templateIn;
    private readonly InputPort[] _slots;
    private readonly OutputPort _out;

    public TemplateNode()
    {
        _templateIn = AddInput<string>("Template");
        _slots =
        [
            AddInput("A", PortTypeCompatibility.Any),
            AddInput("B", PortTypeCompatibility.Any),
            AddInput("C", PortTypeCompatibility.Any),
            AddInput("D", PortTypeCompatibility.Any),
        ];
        _out = AddOutput<string>("Text");
    }

    public string Template
    {
        get => _template;
        set => Set(ref _template, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var template = _templateIn.ValueOr(Template);
        var result = new StringBuilder(template.Length);

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            // "{{" is a literal brace, which is only needed to write "{A}" itself. There is no "}}"
            // escape: JSON closes nested objects with "}}", and must come through unchanged.
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                result.Append(c);
                i++;
                continue;
            }

            // {A}..{D}, either case. Anything else in braces is left as written, so a template can
            // hold JSON without escaping every brace.
            if (c == '{' && i + 2 < template.Length && template[i + 2] == '}'
                && char.ToUpperInvariant(template[i + 1]) - 'A' is var slot && slot is >= 0 and < 4)
            {
                result.Append(NodeValues.ToText(_slots[slot].Value));
                i += 2;
                continue;
            }

            result.Append(c);
        }

        _out.Value = result.ToString();
        return Task.CompletedTask;
    }
}
