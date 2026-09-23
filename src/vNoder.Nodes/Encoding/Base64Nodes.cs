using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Encoding/Base64 Encode", Description = "Encodes text (as UTF-8) or bytes as Base64.")]
public sealed class Base64EncodeNode : NodeBase
{
    private bool _urlSafe;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public Base64EncodeNode()
    {
        _in = AddInput("Data", PortTypeCompatibility.Any);
        _out = AddOutput<string>("Base64");
    }

    /// <summary>The URL- and filename-safe alphabet (<c>-</c> and <c>_</c>), without padding.</summary>
    public bool UrlSafe
    {
        get => _urlSafe;
        set => Set(ref _urlSafe, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var encoded = Convert.ToBase64String(NodeValues.AsBytes(_in.Value, "Base64 encoding"));

        _out.Value = UrlSafe
            ? encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_')
            : encoded;

        return Task.CompletedTask;
    }
}

[NodeDefinition("Encoding/Base64 Decode", Description = "Decodes Base64 (standard or URL-safe) to bytes and UTF-8 text.")]
public sealed class Base64DecodeNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _textOut;

    public Base64DecodeNode()
    {
        _in = AddInput<string>("Base64");
        _bytesOut = AddOutput<byte[]>("Bytes");
        _textOut = AddOutput<string>("Text");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Accept either alphabet, with or without padding and with stray whitespace, so there is no
        // setting to get wrong: the two alphabets differ only in characters the other never uses.
        var text = new string(_in.ValueOr(string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray())
            .Replace('-', '+')
            .Replace('_', '/');

        text = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The input is not valid Base64.");
        }

        _bytesOut.Value = bytes;
        _textOut.Value = Encoding.UTF8.GetString(bytes);
        return Task.CompletedTask;
    }
}
