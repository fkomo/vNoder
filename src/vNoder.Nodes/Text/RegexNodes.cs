using System.Text.RegularExpressions;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// .NET regular expressions. Every node carries the same options inline, from one editor template.
/// An empty pattern is a no-op, as an empty Find is for Replace All, so a graph running continuously
/// is not in an error state while the pattern is mid-edit; an invalid pattern does throw, since there
/// is no sane reading of it.
/// </summary>
public abstract class RegexNode : NodeBase
{
    /// <summary>Guards a continuous run against catastrophic backtracking on a bad pattern.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private string _pattern = string.Empty;
    private bool _ignoreCase;
    private bool _multiline;
    private Regex? _cached;

    protected RegexNode()
    {
        TextIn = AddInput<string>("Text");
        PatternIn = AddInput<string>("Pattern");
    }

    protected InputPort TextIn { get; }

    protected InputPort PatternIn { get; }

    public string Pattern
    {
        get => _pattern;
        set => Set(ref _pattern, value ?? string.Empty);
    }

    public bool IgnoreCase
    {
        get => _ignoreCase;
        set => Set(ref _ignoreCase, value);
    }

    /// <summary>^ and $ match at every line rather than only at the ends of the text.</summary>
    public bool Multiline
    {
        get => _multiline;
        set => Set(ref _multiline, value);
    }

    /// <summary>The compiled pattern, or null when it is empty. Reused while the pattern and options hold.</summary>
    protected Regex? Build()
    {
        var pattern = PatternIn.ValueOr(Pattern);
        if (pattern.Length == 0)
            return null;

        var options = RegexOptions.CultureInvariant
                      | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)
                      | (Multiline ? RegexOptions.Multiline : RegexOptions.None);

        if (_cached is { } cached && cached.ToString() == pattern && cached.Options == options)
            return cached;

        try
        {
            return _cached = new Regex(pattern, options, MatchTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"Invalid pattern: {exception.Message}");
        }
    }

    protected static T Guard<T>(Func<T> match)
    {
        try
        {
            return match();
        }
        catch (RegexMatchTimeoutException)
        {
            throw new InvalidOperationException($"The pattern took longer than {MatchTimeout.TotalSeconds:0} s; it is probably backtracking.");
        }
    }
}

[NodeDefinition("Text/Regex Match", Description = "Tests text against a regular expression, returning the first match and its groups.")]
public sealed class RegexMatchNode : RegexNode
{
    private readonly OutputPort _matchedOut;
    private readonly OutputPort _matchOut;
    private readonly OutputPort _groupsOut;
    private readonly OutputPort _indexOut;

    public RegexMatchNode()
    {
        _matchedOut = AddOutput<bool>("Matched");
        _matchOut = AddOutput<string>("Match");
        _groupsOut = AddOutput<string[]>("Groups");
        _indexOut = AddOutput<double>("Index");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var regex = Build();
        var match = regex is null ? Match.Empty : Guard(() => regex.Match(TextIn.ValueOr(string.Empty)));

        _matchedOut.Value = match.Success;
        _matchOut.Value = match.Success ? match.Value : string.Empty;

        // Groups 1..n: group 0 is the Match output already.
        _groupsOut.Value = match.Success
            ? match.Groups.Cast<Group>().Skip(1).Select(g => g.Value).ToArray()
            : [];
        _indexOut.Value = match.Success ? (double)match.Index : -1d;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Text/Regex Replace", Description = "Replaces every match of a regular expression; $1 or ${name} insert groups.")]
public sealed class RegexReplaceNode : RegexNode
{
    private string _replacement = string.Empty;

    private readonly InputPort _replacementIn;
    private readonly OutputPort _out;
    private readonly OutputPort _countOut;

    public RegexReplaceNode()
    {
        _replacementIn = AddInput<string>("Replacement");
        _out = AddOutput<string>("Text");
        _countOut = AddOutput<double>("Count");
    }

    public string Replacement
    {
        get => _replacement;
        set => Set(ref _replacement, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = TextIn.ValueOr(string.Empty);
        var regex = Build();
        if (regex is null)
        {
            _out.Value = text;
            _countOut.Value = 0d;
            return Task.CompletedTask;
        }

        var replacement = _replacementIn.ValueOr(Replacement);
        var count = 0;
        _out.Value = Guard(() => regex.Replace(text, m =>
        {
            count++;
            return m.Result(replacement);
        }));
        _countOut.Value = (double)count;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Text/Regex Extract All", Description = "Every match of a regular expression (or one group of it) as a list.")]
public sealed class RegexExtractAllNode : RegexNode
{
    private double _group;

    private readonly OutputPort _matchesOut;
    private readonly OutputPort _countOut;

    public RegexExtractAllNode()
    {
        _matchesOut = AddOutput<string[]>("Matches");
        _countOut = AddOutput<double>("Count");
    }

    /// <summary>Which group to collect from each match; 0 is the whole match.</summary>
    public double Group
    {
        get => _group;
        set => Set(ref _group, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var regex = Build();
        var group = double.IsNaN(Group) ? 0 : (int)Math.Clamp(Group, 0, 1000);

        // A group number the pattern does not have reads as empty for every match, like Substring
        // clamping out of range, rather than throwing while the pattern is being edited.
        var matches = regex is null
            ? []
            : Guard(() => regex.Matches(TextIn.ValueOr(string.Empty)).Select(m => m.Groups[group].Value).ToArray());

        _matchesOut.Value = matches;
        _countOut.Value = (double)matches.Length;
        return Task.CompletedTask;
    }
}
