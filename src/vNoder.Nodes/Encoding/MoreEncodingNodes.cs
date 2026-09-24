using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("Encoding/Hex Encode", Description = "Encodes text (as UTF-8) or bytes as hexadecimal.")]
public sealed class HexEncodeNode : NodeBase
{
    private bool _uppercase;
    private string _separator = string.Empty;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public HexEncodeNode()
    {
        _in = AddInput("Data", PortTypeCompatibility.Any);
        _out = AddOutput<string>("Hex");
    }

    public bool Uppercase
    {
        get => _uppercase;
        set => Set(ref _uppercase, value);
    }

    /// <summary>Between bytes, e.g. a space or a colon; empty runs them together.</summary>
    public string Separator
    {
        get => _separator;
        set => Set(ref _separator, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var hex = Convert.ToHexString(NodeValues.AsBytes(_in.Value, "Hex encoding"));
        if (!Uppercase)
            hex = hex.ToLowerInvariant();

        _out.Value = Separator.Length == 0
            ? hex
            : string.Join(Separator, Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
        return Task.CompletedTask;
    }
}

[NodeDefinition("Encoding/Hex Decode", Description = "Decodes hexadecimal to bytes and UTF-8 text; spaces, colons, dashes and 0x are ignored.")]
public sealed class HexDecodeNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _textOut;

    public HexDecodeNode()
    {
        _in = AddInput<string>("Hex");
        _bytesOut = AddOutput<byte[]>("Bytes");
        _textOut = AddOutput<string>("Text");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Accept hex as it is usually pasted: "de ad be ef", "DE:AD:BE:EF", "0xDEADBEEF".
        var text = _in.ValueOr(string.Empty).Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        var digits = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not (':' or '-' or ',')).ToArray());

        if (digits.Length % 2 != 0)
            throw new InvalidOperationException("Hex needs an even number of digits.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(digits);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The input is not valid hex.");
        }

        _bytesOut.Value = bytes;
        _textOut.Value = Encoding.UTF8.GetString(bytes);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Encoding/HTML Escape", Description = "Escapes < > & \" ' as HTML entities.")]
public sealed class HtmlEscapeNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public HtmlEscapeNode()
    {
        _in = AddInput<string>("Text");
        _out = AddOutput<string>("HTML");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = WebUtility.HtmlEncode(_in.ValueOr(string.Empty));
        return Task.CompletedTask;
    }
}

[NodeDefinition("Encoding/HTML Unescape", Description = "Turns HTML entities (&amp;, &lt;, &#39;, &eacute;) back into text.")]
public sealed class HtmlUnescapeNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public HtmlUnescapeNode()
    {
        _in = AddInput<string>("HTML");
        _out = AddOutput<string>("Text");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = WebUtility.HtmlDecode(_in.ValueOr(string.Empty));
        return Task.CompletedTask;
    }
}

public enum CompressionFormat
{
    GZip,
    Deflate,
    ZLib,
    Brotli,
}

[NodeDefinition("Encoding/Compress", Description = "Compresses text (as UTF-8) or bytes with GZip, Deflate, ZLib or Brotli.")]
public sealed class CompressNode : NodeBase
{
    private CompressionFormat _format;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public CompressNode()
    {
        _in = AddInput("Data", PortTypeCompatibility.Any);
        _out = AddOutput<byte[]>("Compressed");
    }

    public CompressionFormat Format
    {
        get => _format;
        set => Set(ref _format, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var bytes = NodeValues.AsBytes(_in.Value, "Compression");
        var format = Format;
        var token = context.CancellationToken;

        _out.Value = await Task.Run(
                () =>
                {
                    using var output = new MemoryStream();
                    using (var stream = Compression.Compress(output, format))
                        stream.Write(bytes);

                    return output.ToArray();
                },
                token)
            .ConfigureAwait(false);
    }
}

[NodeDefinition("Encoding/Decompress", Description = "Decompresses GZip, Deflate, ZLib or Brotli data to bytes and UTF-8 text.")]
public sealed class DecompressNode : NodeBase
{
    private CompressionFormat _format;

    private readonly InputPort _in;
    private readonly OutputPort _bytesOut;
    private readonly OutputPort _textOut;

    public DecompressNode()
    {
        _in = AddInput<byte[]>("Compressed");
        _bytesOut = AddOutput<byte[]>("Bytes");
        _textOut = AddOutput<string>("Text");
    }

    public CompressionFormat Format
    {
        get => _format;
        set => Set(ref _format, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var bytes = _in.ValueOr(Array.Empty<byte>());
        var format = Format;
        var token = context.CancellationToken;

        var result = await Task.Run(() => Compression.Decompress(bytes, format, token), token).ConfigureAwait(false);
        _bytesOut.Value = result;
        _textOut.Value = Encoding.UTF8.GetString(result);
    }
}

internal static class Compression
{
    /// <summary>Stops a small malicious input (a "zip bomb") from filling memory.</summary>
    private const int MaxOutput = 256 * 1024 * 1024;

    public static Stream Compress(Stream output, CompressionFormat format) => format switch
    {
        CompressionFormat.Deflate => new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true),
        CompressionFormat.ZLib => new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true),
        CompressionFormat.Brotli => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
        _ => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
    };

    public static byte[] Decompress(byte[] bytes, CompressionFormat format, CancellationToken token)
    {
        using var input = new MemoryStream(bytes);
        using Stream stream = format switch
        {
            CompressionFormat.Deflate => new DeflateStream(input, CompressionMode.Decompress),
            CompressionFormat.ZLib => new ZLibStream(input, CompressionMode.Decompress),
            CompressionFormat.Brotli => new BrotliStream(input, CompressionMode.Decompress),
            _ => new GZipStream(input, CompressionMode.Decompress),
        };

        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                if (output.Length > MaxOutput)
                    throw new InvalidOperationException($"Decompressed data exceeds {MaxOutput / (1024 * 1024)} MB; refusing to continue.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException($"Not valid {format} data: {exception.Message}");
        }

        return output.ToArray();
    }
}

/// <summary>
/// Reads a JSON Web Token without verifying it. The signature is passed on but never checked, which is
/// what the description says, so nobody mistakes this for validation.
/// </summary>
[NodeDefinition("Encoding/JWT Decode", Description = "Shows a JWT's header, payload and expiry. Does not verify the signature.")]
public sealed class JwtDecodeNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _headerOut;
    private readonly OutputPort _payloadOut;
    private readonly OutputPort _signatureOut;
    private readonly OutputPort _expiresOut;
    private readonly OutputPort _expiredOut;

    public JwtDecodeNode()
    {
        _in = AddInput<string>("Token");
        _headerOut = AddOutput<string>("Header");
        _payloadOut = AddOutput<string>("Payload");
        _signatureOut = AddOutput<string>("Signature");
        _expiresOut = AddOutput<DateTime>("Expires");
        _expiredOut = AddOutput<bool>("Expired");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var token = _in.ValueOr(string.Empty).Trim();

        // Pasted from an Authorization header as often as not.
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();

        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("A JWT has three dot-separated parts.");

        var header = DecodePart(parts[0], "header");
        var payload = DecodePart(parts[1], "payload");

        _headerOut.Value = header.ToJsonString(JsonText.Indented);
        _payloadOut.Value = payload.ToJsonString(JsonText.Indented);
        _signatureOut.Value = parts[2];

        // "exp" is seconds since the epoch (RFC 7519); absent means the token does not expire.
        if (payload is JsonObject claims
            && claims["exp"] is JsonValue exp
            && exp.GetValueKind() == JsonValueKind.Number)
        {
            var expires = DateMath.AddTicks(DateTime.UnixEpoch, exp.GetValue<double>() * TimeSpan.TicksPerSecond);
            _expiresOut.Value = expires.ToLocalTime();
            _expiredOut.Value = expires < DateTime.UtcNow;
        }
        else
        {
            _expiresOut.Value = null;
            _expiredOut.Value = false;
        }

        return Task.CompletedTask;
    }

    private static JsonNode DecodePart(string part, string name)
    {
        var base64 = part.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return JsonNode.Parse(json) ?? throw new InvalidOperationException($"The JWT {name} is empty.");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new InvalidOperationException($"The JWT {name} is not Base64URL-encoded JSON.");
        }
    }
}
