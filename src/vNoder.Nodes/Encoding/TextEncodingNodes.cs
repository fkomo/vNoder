using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

// Text in encodings other than UTF-8, which is what every other node assumes. Encodings are named as
// .NET and the web name them: utf-8, utf-16, utf-16BE, utf-32, us-ascii, latin1 (iso-8859-1), and the
// Windows and DOS code pages (windows-1250, windows-1252, ibm852 …), which come from the code-pages
// provider built into .NET.

internal static class TextEncodings
{
    static TextEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Get(string name, bool byteOrderMark = false)
    {
        name = name.Trim();
        if (name.Length == 0)
            name = "utf-8";

        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"Unknown encoding '{name}'. Try utf-8, utf-16, latin1 or windows-1250.");
        }

        // The Unicode encodings emit a byte order mark only when asked, whatever their default.
        return encoding switch
        {
            UTF8Encoding => new UTF8Encoding(byteOrderMark),
            UnicodeEncoding => new UnicodeEncoding(encoding.CodePage == 1201, byteOrderMark),
            UTF32Encoding => new UTF32Encoding(encoding.CodePage == 12001, byteOrderMark),
            _ => encoding,
        };
    }
}

[NodeDefinition("Encoding/Text To Bytes", Description = "Encodes text as bytes in a chosen encoding: UTF-8, UTF-16, Latin-1, Windows code pages.")]
public sealed class TextToBytesNode : NodeBase
{
    private string _encoding = "utf-8";
    private bool _byteOrderMark;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public TextToBytesNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<byte[]>("Bytes");
    }

    public string Encoding { get => _encoding; set => Set(ref _encoding, value ?? string.Empty); }

    /// <summary>Start with a byte order mark (Unicode encodings only).</summary>
    public bool ByteOrderMark { get => _byteOrderMark; set => Set(ref _byteOrderMark, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var encoding = TextEncodings.Get(Encoding, ByteOrderMark);
        _out.Value = (byte[])[.. encoding.GetPreamble(), .. encoding.GetBytes(_in.ValueOr(string.Empty))];
        return Task.CompletedTask;
    }
}

/// <summary>
/// Decodes bytes as text. A byte order mark at the start wins over the chosen encoding, since it
/// states the encoding outright; characters the encoding cannot map become U+FFFD rather than
/// failing the node.
/// </summary>
[NodeDefinition("Encoding/Bytes To Text", Description = "Decodes bytes as text in a chosen encoding: UTF-8, UTF-16, Latin-1, Windows code pages.")]
public sealed class BytesToTextNode : NodeBase
{
    private string _encoding = "utf-8";

    private readonly InputPort _in;
    private readonly OutputPort _out;
    private readonly OutputPort _detectedOut;

    public BytesToTextNode()
    {
        _in = AddInput<byte[]>("Bytes");
        _out = AddOutput<string>("Text");
        _detectedOut = AddOutput<string>("Encoding");
    }

    public string Encoding { get => _encoding; set => Set(ref _encoding, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var bytes = _in.ValueOr(Array.Empty<byte>());
        var encoding = TextEncodings.Get(Encoding);

        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        _out.Value = reader.ReadToEnd();
        _detectedOut.Value = reader.CurrentEncoding.WebName;
        return Task.CompletedTask;
    }
}

/// <summary>RFC 4648 Base32, the alphabet TOTP secrets use: A–Z and 2–7.</summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> bytes, bool padding)
    {
        var builder = new StringBuilder((bytes.Length + 4) / 5 * 8);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        if (padding)
        {
            while (builder.Length % 8 != 0)
                builder.Append('=');
        }

        return builder.ToString();
    }

    /// <summary>Case, padding, spaces and dashes are ignored, since secrets are often shown grouped.</summary>
    public static byte[] Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            if (c is '=' or ' ' or '-' || char.IsWhiteSpace(c))
                continue;

            var value = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0)
                throw new InvalidOperationException($"'{c}' is not a Base32 character (A–Z, 2–7).");

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}

[NodeDefinition("Encoding/Base32 Encode", Description = "Encodes text (as UTF-8) or bytes as Base32 (RFC 4648), as used for TOTP secrets.")]
public sealed class Base32EncodeNode : NodeBase
{
    private bool _padding = true;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public Base32EncodeNode()
    {
        _in = AddInput("Data", PortTypeCompatibility.Any);
        _out = AddOutput<string>("Base32");
    }

    public bool Padding { get => _padding; set => Set(ref _padding, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = Base32.Encode(NodeValues.AsBytes(_in.Value, "Base32 encoding"), Padding);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Encoding/Base32 Decode", Description = "Decodes Base32 (RFC 4648) to bytes and UTF-8 text; case, padding and spaces are ignored.")]
public sealed class Base32DecodeNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _textOut;

    public Base32DecodeNode()
    {
        _in = AddInput<string>("Base32");
        _bytesOut = AddOutput<byte[]>("Bytes");
        _textOut = AddOutput<string>("Text");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var bytes = Base32.Decode(_in.ValueOr(string.Empty));
        _bytesOut.Value = bytes;
        _textOut.Value = System.Text.Encoding.UTF8.GetString(bytes);
        return Task.CompletedTask;
    }
}
