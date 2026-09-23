using System.Buffers.Binary;
using System.Text;
using SixLabors.ImageSharp;

namespace vNoder.Nodes;

/// <summary>
/// What the image nodes know about encoded images without decoding them: format and size come from
/// the file headers, which is enough to reject a non-image with a useful message, name a file
/// correctly, and report dimensions. Decoding pixels is left to <see cref="ImageConversion"/> and to
/// the view.
/// </summary>
/// <remarks>
/// The seven common formats are recognised here, by signature. Anything else falls back to
/// ImageSharp's detection, which adds TGA, QOI and PBM. TGA has no signature and is detected
/// heuristically, so a few random byte buffers in ten thousand pass as one; decoding then fails with a
/// clear error, so the fallback costs nothing for real input.
/// </remarks>
internal static class ImageData
{
    public const string SupportedFormats = "PNG, JPEG, GIF, BMP, TIFF, ICO, WebP, TGA, QOI or PBM";

    /// <summary>
    /// The image bytes an input carries: bytes as they are, or text holding Base64 or a Base64
    /// <c>data:</c> URI. Null for nothing or empty text.
    /// </summary>
    public static byte[]? FromValue(object? value) => value switch
    {
        null => null,
        byte[] raw => raw,
        string text => FromText(text),
        var other => throw new InvalidOperationException(
            $"An image needs bytes or Base64 text, got {other.GetType().Name}."),
    };

    /// <summary>Like <see cref="FromValue"/>, but also requires the bytes to be a recognised image.</summary>
    public static (byte[] Bytes, string Format)? Require(object? value)
    {
        if (FromValue(value) is not { Length: > 0 } bytes)
            return null;

        var format = Sniff(bytes)
                     ?? throw new InvalidOperationException($"Not a recognised image (expected {SupportedFormats}).");
        return (bytes, format);
    }

    private static byte[]? FromText(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = text.IndexOf(',');
            if (comma < 0 || !text[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only Base64 data: URIs are supported.");

            text = text[(comma + 1)..];
        }

        var compact = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
                compact.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }

        compact.Append('=', (4 - (compact.Length % 4)) % 4);

        try
        {
            return Convert.FromBase64String(compact.ToString());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "Text wired into an image must be Base64 or a data: URI. To use a file, use Image Load.");
        }
    }

    /// <summary>Identifies an image format from its leading bytes; null when it is not an image.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b) => Signature(b) ?? Detected(b);

    private static string? Detected(ReadOnlySpan<byte> b)
    {
        try
        {
            return Image.DetectFormat(b).Name.ToUpperInvariant();
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Signature(ReadOnlySpan<byte> b) => b switch
    {
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "PNG",
        [0xFF, 0xD8, 0xFF, ..] => "JPEG",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "GIF",
        [(byte)'B', (byte)'M', ..] => "BMP",
        [(byte)'I', (byte)'I', 0x2A, 0x00, ..] or [(byte)'M', (byte)'M', 0x00, 0x2A, ..] => "TIFF",
        [0x00, 0x00, 0x01, 0x00, ..] => "ICO",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "WebP",
        _ => null,
    };

    /// <summary>File extensions for a format, the conventional one first.</summary>
    public static IReadOnlyList<string> Extensions(string format) => format switch
    {
        "PNG" => [".png"],
        "JPEG" => [".jpg", ".jpeg", ".jfif", ".jpe"],
        "GIF" => [".gif"],
        "BMP" => [".bmp", ".dib"],
        "TIFF" => [".tif", ".tiff"],
        "ICO" => [".ico"],
        "WebP" => [".webp"],
        "TGA" => [".tga"],
        "QOI" => [".qoi"],
        "PBM" => [".pbm", ".pgm", ".ppm"],
        _ => [],
    };

    /// <summary>
    /// Pixel size read from the headers, or null when the header is truncated or unusual. For ICO it
    /// is the largest image in the file.
    /// </summary>
    public static (int Width, int Height)? Dimensions(ReadOnlySpan<byte> b)
    {
        try
        {
            return Sniff(b) switch
            {
                // IHDR is always the first chunk.
                "PNG" => (BinaryPrimitives.ReadInt32BigEndian(b[16..]), BinaryPrimitives.ReadInt32BigEndian(b[20..])),
                "GIF" => (BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..])),
                "BMP" => Bmp(b),
                "JPEG" => Jpeg(b),
                "WebP" => WebP(b),
                "ICO" => Ico(b),
                "TIFF" => Tiff(b),
                null => null,
                _ => Identified(b),
            };
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Truncated header: the format is known, the size is not.
            return null;
        }
    }

    /// <summary>Size of a format only ImageSharp knows, from its header reader.</summary>
    private static (int, int)? Identified(ReadOnlySpan<byte> b)
    {
        try
        {
            var info = Image.Identify(b);
            return (info.Width, info.Height);
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return null;
        }
    }

    private static (int, int)? Bmp(ReadOnlySpan<byte> b)
    {
        // A 12-byte BITMAPCOREHEADER has 16-bit sizes; every later header has 32-bit ones, and a
        // negative height means the rows are stored top-down.
        if (BinaryPrimitives.ReadInt32LittleEndian(b[14..]) == 12)
            return (BinaryPrimitives.ReadUInt16LittleEndian(b[18..]), BinaryPrimitives.ReadUInt16LittleEndian(b[20..]));

        return (BinaryPrimitives.ReadInt32LittleEndian(b[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b[22..])));
    }

    private static (int, int)? Jpeg(ReadOnlySpan<byte> b)
    {
        // Walk the marker segments to the first start-of-frame, which holds the size.
        var i = 2;
        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF)
                return null;

            var marker = b[i + 1];
            if (marker == 0xFF)
            {
                i++; // Fill byte.
                continue;
            }

            // SOF0..SOF15, except DHT (C4), JPG (C8) and DAC (CC), which share the range.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));

            if (marker is 0x01 or (>= 0xD0 and <= 0xD9))
            {
                i += 2; // Standalone markers carry no length.
                continue;
            }

            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
        }

        return null;
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> b)
    {
        var chunk = Encoding.ASCII.GetString(b[12..16]);
        switch (chunk)
        {
            case "VP8 ": // Lossy: 14-bit sizes after the 3-byte start code.
                return (BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF,
                        BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);

            case "VP8L": // Lossless: two 14-bit (size - 1) fields packed after the signature byte.
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);

            case "VP8X": // Extended: 24-bit (size - 1) canvas fields.
                return (1 + (b[24] | (b[25] << 8) | (b[26] << 16)), 1 + (b[27] | (b[28] << 8) | (b[29] << 16)));

            default:
                return null;
        }
    }

    private static (int, int)? Ico(ReadOnlySpan<byte> b)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
        (int, int)? largest = null;

        for (var entry = 0; entry < count; entry++)
        {
            // A stored 0 means 256, the largest size an ICO directory can express.
            var at = 6 + (entry * 16);
            var width = b[at] == 0 ? 256 : b[at];
            var height = b[at + 1] == 0 ? 256 : b[at + 1];

            if (largest is not { } best || width * height > best.Item1 * best.Item2)
                largest = (width, height);
        }

        return largest;
    }

    private static (int, int)? Tiff(ReadOnlySpan<byte> b)
    {
        var little = b[0] == (byte)'I';

        // The first image file directory: 12-byte entries of (tag, type, count, value).
        var ifd = (int)U32(b, 4, little);
        var entries = U16(b, ifd, little);
        int? width = null, height = null;

        for (var i = 0; i < entries; i++)
        {
            var at = ifd + 2 + (i * 12);
            var tag = U16(b, at, little);
            if (tag is not (256 or 257))
                continue;

            // SHORT (3) or LONG (4), stored inline in the value field.
            var value = U16(b, at + 2, little) == 3 ? U16(b, at + 8, little) : (int)U32(b, at + 8, little);
            if (tag == 256)
                width = value;
            else
                height = value;
        }

        return width is { } w && height is { } h ? (w, h) : null;
    }

    private static uint U32(ReadOnlySpan<byte> b, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt32BigEndian(b[at..]);

    private static ushort U16(ReadOnlySpan<byte> b, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt16BigEndian(b[at..]);
}
