using System.Globalization;
using System.Text.RegularExpressions;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// A multi-way Select: looks the value up in a table of <c>case => result</c> lines and passes on the
/// first matching result, or the default. Mapping an HTTP status to an exit code, a log level to a
/// colour, or a country code to a name then takes one node instead of a chain of Selects.
/// </summary>
/// <remarks>
/// <para>
/// A case is compared with the value as text (<see cref="NodeValues.ToText"/>), so 404 matches a case
/// of <c>404</c>. <c>*</c> and <c>?</c> are wildcards: <c>5*</c> catches every 5xx status. Blank lines
/// and lines starting with <c>#</c> are ignored, and a line without <c>=></c> is a case with an empty
/// result. Cases are tried top to bottom, so put the specific ones first.
/// </para>
/// <para>
/// Results are typed text, like a value typed into a cell: <c>true</c> and <c>false</c> come out as
/// booleans and numbers as numbers, so a result can drive Exit Code or Run If directly; anything else is
/// text. No match is a result, not an error: Matched is false and the default is passed on.
/// </para>
/// </remarks>
[NodeDefinition("Logic/Switch", Description = "Maps a value to a result through a table of 'case => result' lines (wildcards * and ?), with a default.")]
public sealed class SwitchNode : NodeBase
{
    private string _cases = "200 => OK\n404 => Not found\n5* => Server error";
    private string _default = string.Empty;
    private bool _ignoreCase = true;

    private readonly InputPort _valueIn;
    private readonly InputPort _casesIn;
    private readonly InputPort _defaultIn;
    private readonly OutputPort _resultOut;
    private readonly OutputPort _matchedOut;
    private readonly OutputPort _caseOut;

    public SwitchNode()
    {
        _valueIn = AddInput("Value", PortTypeCompatibility.Any);
        _casesIn = AddInput("Cases", PortTypeCompatibility.Any);
        _defaultIn = AddInput("Default", PortTypeCompatibility.Any);
        _resultOut = AddOutput("Result", PortTypeCompatibility.Any);
        _matchedOut = AddOutput<bool>("Matched");
        _caseOut = AddOutput<string>("Case");
    }

    /// <summary>One <c>case => result</c> per line.</summary>
    public string Cases { get => _cases; set => Set(ref _cases, value ?? string.Empty); }

    public string Default { get => _default; set => Set(ref _default, value ?? string.Empty); }

    public bool IgnoreCase { get => _ignoreCase; set => Set(ref _ignoreCase, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var value = NodeValues.ToText(_valueIn.Value).Trim();
        var lines = _casesIn.IsConnected
            ? _casesIn.Value switch
            {
                string[] list => list,
                var other => NodeValues.ToText(other).Split('\n'),
            }
            : Cases.Split('\n');

        foreach (var (pattern, result) in Parse(lines))
        {
            if (!Matches(pattern, value, IgnoreCase))
                continue;

            _resultOut.Value = Typed(result);
            _matchedOut.Value = true;
            _caseOut.Value = pattern;
            return Task.CompletedTask;
        }

        _resultOut.Value = _defaultIn.IsConnected ? _defaultIn.Value : Typed(Default);
        _matchedOut.Value = false;
        _caseOut.Value = null;
        return Task.CompletedTask;
    }

    private static IEnumerable<(string Case, string Result)> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var arrow = line.IndexOf("=>", StringComparison.Ordinal);
            yield return arrow < 0
                ? (line, string.Empty)
                : (line[..arrow].Trim(), line[(arrow + 2)..].Trim());
        }
    }

    private static bool Matches(string pattern, string value, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (pattern.IndexOfAny(['*', '?']) < 0)
            return string.Equals(pattern, value, comparison);

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Singleline);
    }

    /// <summary>A result as the value it reads as: a boolean, a number, or else the text itself.</summary>
    private static object Typed(string text)
    {
        if (bool.TryParse(text, out var flag))
            return flag;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number;

        return text;
    }
}
