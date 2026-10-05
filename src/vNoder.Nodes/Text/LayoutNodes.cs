using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

// Fitting text into a width: a notification title, a table cell, a line of console output. Like the
// other text nodes they clamp rather than throw, so a width mid-edit (0, empty) is a no-op.

/// <summary>Where <see cref="TruncateNode"/> cuts. Saved by number: append only.</summary>
public enum TruncatePosition
{
    /// <summary>Keep the start: "A long sentence…".</summary>
    End,

    /// <summary>Keep both ends: "C:\Users\…\report.pdf", which is what matters in a path.</summary>
    Middle,

    /// <summary>Keep the end: "…the last words".</summary>
    Start,
}

/// <summary>
/// Shortens text to a maximum length, marking the cut with an ellipsis. The length includes the
/// ellipsis, so the result never exceeds it. Text that already fits passes through unchanged, and
/// Truncated says whether it was cut. A cut never splits a surrogate pair (an emoji).
/// </summary>
[NodeDefinition("Text/Truncate", Description = "Shortens text to a length with an ellipsis, cutting at the end, middle (paths) or start.")]
public sealed class TruncateNode : NodeBase
{
    private double _maxLength = 40;
    private string _ellipsis = "…";
    private TruncatePosition _cutAt;
    private bool _wholeWords = true;

    private readonly InputPort _textIn;
    private readonly InputPort _maxLengthIn;
    private readonly OutputPort _out;
    private readonly OutputPort _truncatedOut;

    public TruncateNode()
    {
        _textIn = AddInput("Text", PortTypeCompatibility.Any);
        _maxLengthIn = AddInput<double>("Max Length");
        _out = AddOutput<string>("Text");
        _truncatedOut = AddOutput<bool>("Truncated");
    }

    public double MaxLength { get => _maxLength; set => Set(ref _maxLength, value); }

    public string Ellipsis { get => _ellipsis; set => Set(ref _ellipsis, value ?? string.Empty); }

    // Not "Position": that is the node's place on the canvas, inherited from NodeBase.
    public TruncatePosition CutAt { get => _cutAt; set => Set(ref _cutAt, value); }

    /// <summary>Cut at a space rather than inside a word, when cutting at the end or start.</summary>
    public bool WholeWords { get => _wholeWords; set => Set(ref _wholeWords, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = NodeValues.ToText(_textIn.Value);
        var maxValue = _maxLengthIn.ValueOr(MaxLength);
        var max = double.IsNaN(maxValue) ? text.Length : (int)Math.Clamp(maxValue, 0, int.MaxValue);

        if (text.Length <= max)
        {
            _out.Value = text;
            _truncatedOut.Value = false;
            return Task.CompletedTask;
        }

        // A width too small for the ellipsis gets a bare cut rather than an ellipsis alone.
        var ellipsis = Ellipsis.Length < max ? Ellipsis : string.Empty;
        var keep = max - ellipsis.Length;

        _out.Value = CutAt switch
        {
            TruncatePosition.Middle => Head(text, keep - keep / 2) + ellipsis + Tail(text, keep / 2),
            TruncatePosition.Start => ellipsis + TrimToWord(Tail(text, keep), fromStart: true),
            _ => TrimToWord(Head(text, keep), fromStart: false) + ellipsis,
        };
        _truncatedOut.Value = true;
        return Task.CompletedTask;

        string TrimToWord(string part, bool fromStart)
        {
            if (!WholeWords)
                return part;

            // Back off to the nearest space, unless that would throw away most of what fits.
            var space = fromStart ? part.IndexOf(' ') : part.LastIndexOf(' ');
            if (space < 0 || (fromStart ? part.Length - space : space) < part.Length / 2)
                return part;

            return fromStart ? part[(space + 1)..] : part[..space].TrimEnd();
        }
    }

    private static string Head(string text, int length)
    {
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
            length--;

        return text[..Math.Max(0, length)];
    }

    private static string Tail(string text, int length)
    {
        var start = text.Length - Math.Max(0, length);
        if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]))
            start++;

        return text[start..];
    }
}

/// <summary>
/// Breaks text into lines no wider than a width, at spaces: for console output (CLI Write), plain-text
/// email, or a fixed-width report. Existing line breaks are kept, each line wrapped on its own, and a
/// word longer than the width is split unless Break Long Words is off.
/// </summary>
[NodeDefinition("Text/Wrap", Description = "Wraps text to a width at spaces, keeping existing line breaks.")]
public sealed class WrapNode : NodeBase
{
    private double _width = 80;
    private bool _breakLongWords = true;

    private readonly InputPort _textIn;
    private readonly InputPort _widthIn;
    private readonly OutputPort _out;
    private readonly OutputPort _linesOut;

    public WrapNode()
    {
        _textIn = AddInput("Text", PortTypeCompatibility.Any);
        _widthIn = AddInput<double>("Width");
        _out = AddOutput<string>("Text");
        _linesOut = AddOutput<string[]>("Lines");
    }

    public double Width { get => _width; set => Set(ref _width, value); }

    public bool BreakLongWords { get => _breakLongWords; set => Set(ref _breakLongWords, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = NodeValues.ToText(_textIn.Value).Replace("\r\n", "\n");
        var widthValue = _widthIn.ValueOr(Width);
        var width = double.IsNaN(widthValue) ? 0 : (int)Math.Clamp(widthValue, 0, 100_000);

        string[] lines = width < 1
            ? text.Split('\n')
            : [.. text.Split('\n').SelectMany(line => WrapLine(line, width, BreakLongWords))];

        _linesOut.Value = lines;
        _out.Value = string.Join(Environment.NewLine, lines);
        return Task.CompletedTask;
    }

    private static IEnumerable<string> WrapLine(string line, int width, bool breakLongWords)
    {
        if (line.Length <= width)
        {
            yield return line;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var rest = word;
            while (breakLongWords && rest.Length > width)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                yield return rest[..width];
                rest = rest[width..];
            }

            if (current.Length > 0 && current.Length + 1 + rest.Length > width)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(' ');
            current.Append(rest);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}
