using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Styles offered by <see cref="CaseConvertNode"/>. Saved by number: append, never reorder.</summary>
public enum CaseStyle
{
    /// <summary>camelCase</summary>
    Camel,

    /// <summary>PascalCase</summary>
    Pascal,

    /// <summary>snake_case</summary>
    Snake,

    /// <summary>kebab-case</summary>
    Kebab,

    /// <summary>CONSTANT_CASE</summary>
    Constant,

    /// <summary>Title Case</summary>
    Title,

    /// <summary>Sentence case</summary>
    Sentence,

    /// <summary>url-slug: lower case, accents removed, anything else dropped.</summary>
    Slug,
}

/// <summary>
/// Re-cases text between naming conventions. Words are found at separators (spaces, _, -, .) and at
/// case changes, so "XMLHttpRequest", "xml_http_request" and "Xml Http Request" all read as the same
/// three words.
/// </summary>
[NodeDefinition("Text/Case Convert", Description = "Converts between camelCase, PascalCase, snake_case, kebab-case, CONSTANT_CASE, Title Case, Sentence case and url-slug.")]
public sealed partial class CaseConvertNode : NodeBase
{
    private CaseStyle _style;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public CaseConvertNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<string>("Text");
    }

    public CaseStyle Style
    {
        get => _style;
        set => Set(ref _style, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);
        if (Style == CaseStyle.Slug)
            text = TextNormalization.RemoveAccents(text);

        var words = Words().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToArray();
        _out.Value = Style switch
        {
            CaseStyle.Camel => string.Concat(words.Select((w, i) => i == 0 ? w : Capitalize(w))),
            CaseStyle.Pascal => string.Concat(words.Select(Capitalize)),
            CaseStyle.Snake => string.Join('_', words),
            CaseStyle.Kebab or CaseStyle.Slug => string.Join('-', words),
            CaseStyle.Constant => string.Join('_', words).ToUpperInvariant(),
            CaseStyle.Title => string.Join(' ', words.Select(Capitalize)),
            _ => Capitalize(string.Join(' ', words)),
        };

        return Task.CompletedTask;
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    // An upper-case run before a capitalised word ("XML" in XMLHttp), a capitalised or lower-case word,
    // a trailing upper-case run, or a run of digits.
    [GeneratedRegex(@"\p{Lu}+(?=\p{Lu}\p{Ll})|\p{Lu}?\p{Ll}+|\p{Lu}+|\p{Nd}+")]
    private static partial Regex Words();
}

/// <summary>Unicode normalization forms, or none.</summary>
public enum NormalizationKind
{
    None,
    NFC,
    NFD,
    NFKC,
    NFKD,
}

/// <summary>
/// Cleans text up for comparison or matching. Removing accents decomposes each character and drops
/// the combining marks, so "Příliš žluťoučký" reads "Prilis zlutoucky"; letters with no decomposition
/// (ß, ø, đ) are left alone.
/// </summary>
[NodeDefinition("Text/Normalize", Description = "Removes accents, collapses whitespace and applies Unicode normalization (NFC, NFD, NFKC, NFKD).")]
public sealed partial class NormalizeTextNode : NodeBase
{
    private bool _removeAccents = true;
    private bool _collapseWhitespace = true;
    private NormalizationKind _form = NormalizationKind.NFC;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public NormalizeTextNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<string>("Text");
    }

    public bool RemoveAccents { get => _removeAccents; set => Set(ref _removeAccents, value); }

    /// <summary>Trims the ends and turns every run of whitespace, line breaks included, into one space.</summary>
    public bool CollapseWhitespace { get => _collapseWhitespace; set => Set(ref _collapseWhitespace, value); }

    public NormalizationKind Form { get => _form; set => Set(ref _form, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);

        if (RemoveAccents)
            text = TextNormalization.RemoveAccents(text);

        if (CollapseWhitespace)
            text = Whitespace().Replace(text, " ").Trim();

        if (Form != NormalizationKind.None)
        {
            text = text.Normalize(Form switch
            {
                NormalizationKind.NFD => NormalizationForm.FormD,
                NormalizationKind.NFKC => NormalizationForm.FormKC,
                NormalizationKind.NFKD => NormalizationForm.FormKD,
                _ => NormalizationForm.FormC,
            });
        }

        _out.Value = text;
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

internal static class TextNormalization
{
    public static string RemoveAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>User-perceived characters, so an emoji or an accented letter built from parts is one.</summary>
    public static IEnumerable<string> TextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            yield return enumerator.GetTextElement();
    }
}

/// <summary>
/// A line-by-line diff (Myers' algorithm, the one git uses), as lines prefixed "+ " for added,
/// "- " for removed and "  " for unchanged. Up to 2,000 changed lines; beyond that the texts are
/// treated as too different to diff usefully, which also bounds the memory the algorithm needs.
/// </summary>
[NodeDefinition("Text/Diff", Description = "Line diff between two texts, with + and - markers and counts of added and removed lines.")]
public sealed class TextDiffNode : NodeBase
{
    private const int MaxChanges = 2_000;

    private bool _onlyChanges;
    private bool _ignoreCase;
    private bool _ignoreWhitespace;

    private readonly InputPort _aIn;
    private readonly InputPort _bIn;
    private readonly OutputPort _diffOut;
    private readonly OutputPort _changedOut;
    private readonly OutputPort _addedOut;
    private readonly OutputPort _removedOut;

    public TextDiffNode()
    {
        _aIn = AddInput<string>("Old");
        _bIn = AddInput<string>("New");
        _diffOut = AddOutput<string>("Diff");
        _changedOut = AddOutput<bool>("Changed");
        _addedOut = AddOutput<double>("Added");
        _removedOut = AddOutput<double>("Removed");
    }

    /// <summary>Leave unchanged lines out of the diff text.</summary>
    public bool OnlyChanges { get => _onlyChanges; set => Set(ref _onlyChanges, value); }

    public bool IgnoreCase { get => _ignoreCase; set => Set(ref _ignoreCase, value); }

    /// <summary>Compare lines with their ends trimmed and inner runs of whitespace as one space.</summary>
    public bool IgnoreWhitespace { get => _ignoreWhitespace; set => Set(ref _ignoreWhitespace, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var a = Lines(_aIn.ValueOr(string.Empty));
        var b = Lines(_bIn.ValueOr(string.Empty));

        var keyA = a.Select(Key).ToArray();
        var keyB = b.Select(Key).ToArray();
        var edits = Myers(keyA, keyB);

        var builder = new StringBuilder();
        int added = 0, removed = 0;
        foreach (var (kind, index) in edits)
        {
            switch (kind)
            {
                case '+':
                    added++;
                    builder.Append("+ ").AppendLine(b[index]);
                    break;
                case '-':
                    removed++;
                    builder.Append("- ").AppendLine(a[index]);
                    break;
                default:
                    if (!OnlyChanges)
                        builder.Append("  ").AppendLine(a[index]);
                    break;
            }
        }

        _diffOut.Value = builder.ToString().TrimEnd('\r', '\n');
        _changedOut.Value = added + removed > 0;
        _addedOut.Value = (double)added;
        _removedOut.Value = (double)removed;
        return Task.CompletedTask;
    }

    private static string[] Lines(string text) =>
        text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');

    private string Key(string line)
    {
        if (IgnoreWhitespace)
            line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return IgnoreCase ? line.ToUpperInvariant() : line;
    }

    /// <summary>The edit script: ('=', index in a), ('-', index in a) or ('+', index in b), in order.</summary>
    private static List<(char Kind, int Index)> Myers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        var offset = max + 1;
        var v = new int[(2 * max) + 3];

        // A snapshot of v before each step d, covering k in [-(d+1), d+1]: all backtracking reads.
        var trace = new List<int[]>();

        for (var d = 0; d <= max; d++)
        {
            if (d > MaxChanges)
                throw new InvalidOperationException($"The texts differ in more than {MaxChanges:N0} lines; too different to diff.");

            trace.Add(v.AsSpan(offset - d - 1, (2 * d) + 3).ToArray());

            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;

                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                    return Backtrack(trace, n, m);
            }
        }

        return [];
    }

    private static List<(char, int)> Backtrack(List<int[]> trace, int n, int m)
    {
        var edits = new List<(char, int)>();
        int x = n, y = m;

        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var snapshot = trace[d];
            int V(int k) => snapshot[k + d + 1];

            var k = x - y;
            var previousK = k == -d || (k != d && V(k - 1) < V(k + 1)) ? k + 1 : k - 1;
            var previousX = V(previousK);
            var previousY = previousX - previousK;

            while (x > previousX && y > previousY)
            {
                edits.Add(('=', x - 1));
                x--;
                y--;
            }

            if (d > 0)
            {
                if (x == previousX)
                    edits.Add(('+', y - 1));
                else
                    edits.Add(('-', x - 1));
            }

            x = previousX;
            y = previousY;
        }

        edits.Reverse();
        return edits;
    }
}

/// <summary>
/// Edit distance (Levenshtein: insertions, deletions and substitutions, one each) and a similarity
/// from 0 to 1. Compared by user-perceived character, so an emoji counts once.
/// </summary>
[NodeDefinition("Text/Similarity", Description = "Levenshtein edit distance between two texts, and a similarity from 0 (nothing alike) to 1 (equal).")]
public sealed class TextSimilarityNode : NodeBase
{
    private bool _ignoreCase;

    private readonly InputPort _aIn;
    private readonly InputPort _bIn;
    private readonly OutputPort _distanceOut;
    private readonly OutputPort _similarityOut;

    public TextSimilarityNode()
    {
        _aIn = AddInput<string>("A");
        _bIn = AddInput<string>("B");
        _distanceOut = AddOutput<double>("Distance");
        _similarityOut = AddOutput<double>("Similarity");
    }

    public bool IgnoreCase { get => _ignoreCase; set => Set(ref _ignoreCase, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var a = Elements(_aIn.ValueOr(string.Empty));
        var b = Elements(_bIn.ValueOr(string.Empty));

        // Quadratic in time; a hundred million cells is a couple of seconds at worst.
        if ((long)a.Length * b.Length > 100_000_000)
            throw new InvalidOperationException("The texts are too long to compare (over 100 million character pairs).");

        var distance = Levenshtein(a, b);
        var longest = Math.Max(a.Length, b.Length);

        _distanceOut.Value = (double)distance;
        _similarityOut.Value = longest == 0 ? 1d : 1d - ((double)distance / longest);
        return Task.CompletedTask;
    }

    private string[] Elements(string text) =>
        TextNormalization.TextElements(IgnoreCase ? text.ToUpperInvariant() : text).ToArray();

    private static int Levenshtein(string[] a, string[] b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

[NodeDefinition("Text/Reverse", Description = "Reverses text, keeping emoji and accented letters intact.")]
public sealed class ReverseTextNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public ReverseTextNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<string>("Text");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = string.Concat(TextNormalization.TextElements(_in.ValueOr(string.Empty)).Reverse());
        return Task.CompletedTask;
    }
}

[NodeDefinition("Text/Word Count", Description = "Counts the words, characters and lines of a text.")]
public sealed partial class WordCountNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _wordsOut;
    private readonly OutputPort _charactersOut;
    private readonly OutputPort _linesOut;

    public WordCountNode()
    {
        _in = AddInput<string>("Text");
        _wordsOut = AddOutput<double>("Words");
        _charactersOut = AddOutput<double>("Characters");
        _linesOut = AddOutput<double>("Lines");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);
        _wordsOut.Value = (double)Word().Count(text);
        _charactersOut.Value = (double)TextNormalization.TextElements(text).Count();
        _linesOut.Value = text.Length == 0 ? 0d : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length;
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex Word();
}

/// <summary>Markdown to HTML with Markdig's common extensions: tables, task lists, footnotes, auto-links.</summary>
[NodeDefinition("Text/Markdown To HTML", Description = "Renders Markdown (with tables, task lists and other common extensions) as HTML.")]
public sealed class MarkdownToHtmlNode : NodeBase
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public MarkdownToHtmlNode()
    {
        _in = AddInput<string>("Markdown");
        _out = AddOutput<string>("HTML");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = Markdown.ToHtml(_in.ValueOr(string.Empty), Pipeline);
        return Task.CompletedTask;
    }
}
