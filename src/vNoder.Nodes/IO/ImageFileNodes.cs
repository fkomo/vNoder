using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("IO/Image Load", Description = "Loads an image file (PNG, JPEG, GIF, BMP, TIFF, ICO, WebP, TGA, QOI, PBM) as bytes, with its format and size.")]
public sealed class ImageLoadNode : NodeBase
{
    private string _path = string.Empty;

    private readonly InputPort _pathIn;
    private readonly OutputPort _imageOut;
    private readonly OutputPort _formatOut;
    private readonly OutputPort _widthOut;
    private readonly OutputPort _heightOut;

    public ImageLoadNode()
    {
        _pathIn = AddInput<string>("Path");
        _imageOut = AddOutput<byte[]>("Image");
        _formatOut = AddOutput<string>("Format");
        _widthOut = AddOutput<double>("Width");
        _heightOut = AddOutput<double>("Height");

        AddRunGate();
    }

    /// <summary>Inline path, used when the Path input is not wired.</summary>
    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var path = ImageFiles.Resolve(_pathIn.IsConnected ? _pathIn.ValueOr(string.Empty) : Path);
        var bytes = await File.ReadAllBytesAsync(path, context.CancellationToken).ConfigureAwait(false);

        // Unlike File Read, a file that is not an image is an error here: that is the point of the node.
        var format = ImageData.Sniff(bytes) ?? throw new InvalidOperationException(
            $"{System.IO.Path.GetFileName(path)} is not a recognised image (expected {ImageData.SupportedFormats}).");
        var size = ImageData.Dimensions(bytes);

        _imageOut.Value = bytes;
        _formatOut.Value = format;
        _widthOut.Value = size is { } s ? (double)s.Width : null;
        _heightOut.Value = size is { } t ? (double)t.Height : null;
    }
}

/// <summary>What <see cref="ImageSaveNode"/> does when the file is already there.</summary>
public enum ExistingFileMode
{
    /// <summary>Replace it.</summary>
    Overwrite,

    /// <summary>Leave it alone and report Success = false.</summary>
    Skip,

    /// <summary>Save as "name (2).png", "name (3).png", … — a series, one file per pass.</summary>
    AddNumber,
}

/// <summary>
/// Saves an image as a file, in the format its extension names: a PNG saved as <c>photo.jpg</c> is
/// converted to JPEG (at <see cref="Quality"/>), never written as a PNG with the wrong name. With no
/// extension the image is saved as it is, and the extension for its format is added. When the format
/// already matches, the bytes are written exactly as they arrive, with no re-encoding. Like File
/// Write, this writes on every pass of a continuous run.
/// </summary>
[NodeDefinition("IO/Image Save", Description = "Saves an image to a file, converting it to the format the extension names.")]
public sealed class ImageSaveNode : NodeBase
{
    private string _path = string.Empty;
    private ExistingFileMode _ifExists;
    private double _quality = ImageConversion.DefaultQuality;

    private readonly InputPort _imageIn;
    private readonly InputPort _pathIn;
    private readonly OutputPort _successOut;
    private readonly OutputPort _pathOut;

    public ImageSaveNode()
    {
        _imageIn = AddInput("Image", PortTypeCompatibility.Any);
        _pathIn = AddInput<string>("Path");
        _successOut = AddOutput<bool>("Success");
        _pathOut = AddOutput<string>("Saved As");

        AddRunGate();
    }

    /// <summary>Inline path, used when the Path input is not wired. The extension may be left off.</summary>
    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    public ExistingFileMode IfExists
    {
        get => _ifExists;
        set => Set(ref _ifExists, value);
    }

    /// <summary>1–100, used only when saving converts to JPEG or WebP.</summary>
    public double Quality
    {
        get => _quality;
        set => Set(ref _quality, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        _successOut.Value = false;
        _pathOut.Value = null;

        var (bytes, format) = ImageData.Require(_imageIn.Value)
                              ?? throw new InvalidOperationException("No image to save.");

        var path = (_pathIn.IsConnected ? _pathIn.ValueOr(string.Empty) : Path).Trim().Trim('"');
        if (path.Length == 0)
            throw new InvalidOperationException("No file path set.");

        path = Environment.ExpandEnvironmentVariables(path);

        // Decide the target first, but convert only once it is known the file will be written.
        ImageFileFormat? convertTo = null;
        var extension = System.IO.Path.GetExtension(path);
        if (extension.Length == 0)
        {
            path += ImageData.Extensions(format)[0];
        }
        else if (!ImageData.Extensions(format).Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            convertTo = ImageConversion.ForExtension(extension) ?? throw new InvalidOperationException(
                $"{extension} is not an image format Image Save can write. "
                + $"Use {string.Join(", ", Enum.GetValues<ImageFileFormat>().Select(f => ImageData.Extensions(ImageConversion.Name(f))[0]))}.");
        }

        if (File.Exists(path))
        {
            switch (IfExists)
            {
                case ExistingFileMode.Skip:
                    _pathOut.Value = path;
                    return;

                case ExistingFileMode.AddNumber:
                    path = FilePaths.NextFreeName(path);
                    break;
            }
        }

        if (convertTo is { } target)
        {
            var quality = (int)(double.IsNaN(Quality) ? ImageConversion.DefaultQuality : Quality);
            var token = context.CancellationToken;
            bytes = await Task.Run(() => ImageConversion.Convert(bytes, target, quality, token), token).ConfigureAwait(false);
        }

        FilePaths.EnsureDirectory(path);
        await File.WriteAllBytesAsync(path, bytes, context.CancellationToken).ConfigureAwait(false);
        _successOut.Value = true;
        _pathOut.Value = path;
    }
}

internal static class FilePaths
{
    /// <summary>
    /// Creates the folder a file is about to be written into, and any missing parents. A no-op when it
    /// exists, and for a bare file name, which lands in the working directory.
    /// </summary>
    public static void EnsureDirectory(string filePath)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>The first of "name (2).ext", "name (3).ext", … that does not exist yet.</summary>
    public static string NextFreeName(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = System.IO.Path.GetExtension(path);

        for (var n = 2; ; n++)
        {
            var candidate = System.IO.Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}

internal static class ImageFiles
{
    public static string Resolve(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0)
            throw new InvalidOperationException("No file path set.");

        path = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}");

        return path;
    }
}
