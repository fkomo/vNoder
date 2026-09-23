using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Pbm;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Qoi;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace vNoder.Nodes;

/// <summary>Formats an image can be converted to: everything ImageSharp can encode.</summary>
public enum ImageFileFormat
{
    PNG,
    JPEG,
    WebP,
    GIF,
    BMP,
    TIFF,
    TGA,
    QOI,
    PBM,
}

/// <summary>
/// Re-encoding between formats, through ImageSharp. This is the one place Nodes decodes pixels; see
/// <see cref="ImageData"/> for everything that is done from the headers alone.
/// </summary>
/// <remarks>
/// ImageSharp is pinned to 3.1 in <c>Directory.Packages.props</c>: 4.x adds a build-time license-key
/// check that fails Release builds without a key. It reads and writes every format here except ICO,
/// which it can neither decode nor encode, so an ICO passes through the image nodes but cannot be
/// converted.
/// </remarks>
internal static class ImageConversion
{
    public const int DefaultQuality = 90;

    /// <summary>The name <see cref="ImageData.Sniff"/> reports for a format, so the two agree.</summary>
    public static string Name(ImageFileFormat format) => format.ToString();

    /// <summary>The format a file extension asks for, or null when it is not one this can write.</summary>
    public static ImageFileFormat? ForExtension(string extension)
    {
        foreach (var format in Enum.GetValues<ImageFileFormat>())
        {
            if (ImageData.Extensions(Name(format)).Contains(extension, StringComparer.OrdinalIgnoreCase))
                return format;
        }

        return null;
    }

    /// <summary>
    /// Decodes <paramref name="source"/> and encodes it as <paramref name="target"/>.
    /// <paramref name="quality"/> (1–100) applies to JPEG and WebP; WebP at 100 is lossless.
    /// </summary>
    public static byte[] Convert(byte[] source, ImageFileFormat target, int quality, CancellationToken cancellationToken)
    {
        quality = Math.Clamp(quality, 1, 100);

        Image image;
        try
        {
            image = Image.Load(source);
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            var format = ImageData.Sniff(source);
            throw new InvalidOperationException(format == "ICO"
                ? "ICO images cannot be converted; ImageSharp does not read ICO."
                : $"The image could not be decoded{(format is null ? string.Empty : $" as {format}")}: {exception.Message}");
        }

        using (image)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // These formats have no alpha channel. Left alone, transparent pixels would come out
            // black (their colour channels are usually zero), so composite onto white first, which is
            // what a transparent image looks like on a page.
            if (target is ImageFileFormat.JPEG or ImageFileFormat.BMP or ImageFileFormat.PBM)
                image.Mutate(x => x.BackgroundColor(Color.White));

            using var output = new MemoryStream();
            image.Save(output, Encoder(target, quality));
            return output.ToArray();
        }
    }

    private static IImageEncoder Encoder(ImageFileFormat format, int quality) => format switch
    {
        ImageFileFormat.JPEG => new JpegEncoder { Quality = quality },
        ImageFileFormat.WebP => new WebpEncoder
        {
            Quality = quality,
            FileFormat = quality >= 100 ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy,
        },
        ImageFileFormat.GIF => new GifEncoder(),
        ImageFileFormat.BMP => new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 },
        ImageFileFormat.TIFF => new TiffEncoder(),
        ImageFileFormat.TGA => new TgaEncoder(),
        ImageFileFormat.QOI => new QoiEncoder(),
        ImageFileFormat.PBM => new PbmEncoder(),
        _ => new PngEncoder(),
    };
}
