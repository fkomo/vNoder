using System.Net;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Encoding/URL Encode", Description = "Percent-encodes text for use in a URL.")]
public sealed class UrlEncodeNode : NodeBase
{
    private bool _formEncoding;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public UrlEncodeNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<string>("Encoded");
    }

    /// <summary>
    /// Form encoding (<c>application/x-www-form-urlencoded</c>): spaces become <c>+</c>. Otherwise
    /// RFC 3986 escaping, where a space is <c>%20</c> — right for paths and most query values.
    /// </summary>
    public bool FormEncoding
    {
        get => _formEncoding;
        set => Set(ref _formEncoding, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);
        _out.Value = FormEncoding ? WebUtility.UrlEncode(text) : Uri.EscapeDataString(text);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Encoding/URL Decode", Description = "Decodes percent-encoded text.")]
public sealed class UrlDecodeNode : NodeBase
{
    private bool _formEncoding;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public UrlDecodeNode()
    {
        _in = AddInput<string>("Encoded");
        _out = AddOutput<string>("Text");
    }

    /// <summary>Form encoding: <c>+</c> decodes to a space. Otherwise a <c>+</c> stays a <c>+</c>.</summary>
    public bool FormEncoding
    {
        get => _formEncoding;
        set => Set(ref _formEncoding, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _in.ValueOr(string.Empty);

        // Neither decoder throws: a malformed escape is left as written.
        _out.Value = FormEncoding ? WebUtility.UrlDecode(text) : Uri.UnescapeDataString(text);
        return Task.CompletedTask;
    }
}
