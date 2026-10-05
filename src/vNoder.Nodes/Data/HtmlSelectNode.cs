using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>What <see cref="HtmlSelectNode"/> reads from each matched element. Saved by number: append only.</summary>
public enum HtmlValueKind
{
    /// <summary>The visible text, whitespace collapsed.</summary>
    Text,

    /// <summary>The value of the attribute named in Attribute (href, src, content …).</summary>
    Attribute,

    /// <summary>The element's contents as HTML.</summary>
    InnerHtml,

    /// <summary>The element itself as HTML.</summary>
    OuterHtml,
}

/// <summary>
/// Reads values from an HTML page by CSS selector: <c>h1</c>, <c>a.download[href$=".zip"]</c>,
/// <c>meta[property="og:title"]</c>, <c>table tr td:nth-child(2)</c>. The scraping half of HTTP
/// Request, which brings the page; XML Get cannot read real-world HTML, and a regex breaks on it.
/// </summary>
/// <remarks>
/// The page is parsed the way a browser parses it (AngleSharp), so unclosed tags and other common
/// mistakes are fine; scripts are not run, so content a page builds in JavaScript is not there.
/// Matching nothing is a result (Found = false); an invalid selector throws. With Base URL set,
/// relative href and src attributes come out as absolute URLs, ready for Download To File.
/// </remarks>
[NodeDefinition("Data/HTML Select", Description = "Reads text, attributes or HTML from a web page by CSS selector, e.g. h1 or a[href].")]
public sealed partial class HtmlSelectNode : NodeBase
{
    private string _selector = "title";
    private HtmlValueKind _kind;
    private string _attribute = "href";
    private string _baseUrl = string.Empty;

    private readonly InputPort _htmlIn;
    private readonly InputPort _selectorIn;
    private readonly InputPort _baseUrlIn;
    private readonly OutputPort _valueOut;
    private readonly OutputPort _valuesOut;
    private readonly OutputPort _countOut;
    private readonly OutputPort _foundOut;

    public HtmlSelectNode()
    {
        _htmlIn = AddInput<string>("HTML");
        _selectorIn = AddInput<string>("Selector");
        _baseUrlIn = AddInput<string>("Base URL");
        _valueOut = AddOutput<string>("Value");
        _valuesOut = AddOutput<string[]>("Values");
        _countOut = AddOutput<double>("Count");
        _foundOut = AddOutput<bool>("Found");
    }

    public string Selector { get => _selector; set => Set(ref _selector, value ?? string.Empty); }

    public HtmlValueKind Kind { get => _kind; set => Set(ref _kind, value); }

    /// <summary>The attribute read when <see cref="Kind"/> is Attribute.</summary>
    public string Attribute { get => _attribute; set => Set(ref _attribute, value ?? string.Empty); }

    /// <summary>The page's own URL, to make relative links absolute; empty leaves them as they are.</summary>
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value ?? string.Empty); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var html = _htmlIn.ValueOr(string.Empty);
        var selector = _selectorIn.ValueOr(Selector).Trim();
        if (selector.Length == 0)
            throw new InvalidOperationException("No CSS selector set.");

        var baseText = (_baseUrlIn.IsConnected ? _baseUrlIn.ValueOr(string.Empty) : BaseUrl).Trim();
        Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri);

        var document = await new HtmlParser().ParseDocumentAsync(html, context.CancellationToken).ConfigureAwait(false);

        IHtmlCollection<IElement> matches;
        try
        {
            matches = document.QuerySelectorAll(selector);
        }
        catch (DomException exception)
        {
            throw new InvalidOperationException($"Invalid CSS selector '{selector}': {exception.Message}");
        }

        var attribute = Attribute.Trim();
        var values = matches
            .Select(element => Kind switch
            {
                HtmlValueKind.Attribute => element.GetAttribute(attribute) is { } raw ? Resolve(raw, attribute, baseUri) : null,
                HtmlValueKind.InnerHtml => element.InnerHtml,
                HtmlValueKind.OuterHtml => element.OuterHtml,
                _ => Whitespace().Replace(element.TextContent, " ").Trim(),
            })
            .OfType<string>()
            .ToArray();

        _valuesOut.Value = values;
        _valueOut.Value = values.Length > 0 ? values[0] : null;
        _countOut.Value = (double)values.Length;
        _foundOut.Value = values.Length > 0;
    }

    /// <summary>A link attribute against the base URL; anything else, or no base, unchanged.</summary>
    private static string Resolve(string value, string attribute, Uri? baseUri) =>
        baseUri is not null
        && attribute.ToLowerInvariant() is "href" or "src" or "action" or "poster" or "data-src"
        && Uri.TryCreate(baseUri, value.Trim(), out var absolute)
            ? absolute.ToString()
            : value;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
