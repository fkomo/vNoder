using System.IO.Compression;
using System.Security.Cryptography;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Hashes a file as it streams from disk, so a multi-gigabyte file costs a buffer's worth of memory
/// rather than its own size (Hash needs the whole thing loaded by File Read first). The digest is
/// the same hex Hash gives, so the two can be compared.
/// </summary>
[NodeDefinition("IO/File Hash", Description = "Hashes a file of any size without loading it: MD5, SHA-1, SHA-256, SHA-384, SHA-512 or CRC32.")]
public sealed class FileHashNode : NodeBase
{
    private string _path = string.Empty;
    private HashAlgorithmKind _algorithm = HashAlgorithmKind.SHA256;

    private readonly InputPort _pathIn;
    private readonly OutputPort _digestOut;
    private readonly OutputPort _sizeOut;

    public FileHashNode()
    {
        _pathIn = AddInput<string>("Path");
        _digestOut = AddOutput<string>("Digest");
        _sizeOut = AddOutput<double>("Size");

        AddRunGate();
    }

    public string Path { get => _path; set => Set(ref _path, value ?? string.Empty); }

    public HashAlgorithmKind Algorithm { get => _algorithm; set => Set(ref _algorithm, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var path = FilePaths.Existing(_pathIn.IsConnected ? _pathIn.ValueOr(string.Empty) : Path);
        var algorithm = Algorithm;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16, useAsync: true);

        byte[] digest;
        if (algorithm == HashAlgorithmKind.CRC32)
        {
            var crc = new Crc32();
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await stream.ReadAsync(buffer, context.CancellationToken).ConfigureAwait(false)) > 0)
                crc.Append(buffer.AsSpan(0, read));

            digest = crc.GetHash();
        }
        else
        {
            var name = algorithm switch
            {
                HashAlgorithmKind.MD5 => HashAlgorithmName.MD5,
                HashAlgorithmKind.SHA1 => HashAlgorithmName.SHA1,
                HashAlgorithmKind.SHA384 => HashAlgorithmName.SHA384,
                HashAlgorithmKind.SHA512 => HashAlgorithmName.SHA512,
                _ => HashAlgorithmName.SHA256,
            };

            using var hash = IncrementalHash.CreateHash(name);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await stream.ReadAsync(buffer, context.CancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);

            digest = hash.GetHashAndReset();
        }

        _digestOut.Value = Convert.ToHexString(digest).ToLowerInvariant();
        _sizeOut.Value = (double)stream.Length;
    }
}

/// <summary>Compression levels offered by <see cref="ZipCreateNode"/>, as .NET names them.</summary>
public enum ZipLevel
{
    Optimal,
    Fastest,
    SmallestSize,
    NoCompression,
}

/// <summary>
/// Zips a folder (its contents, not the folder itself), a single file, or a list of files. A list keeps
/// only file names, so two files with the same name from different folders would collide; the second
/// is refused rather than silently overwriting the first.
/// </summary>
[NodeDefinition("IO/Zip Create", Description = "Creates a .zip from a folder, a file, or a list of files.")]
public sealed class ZipCreateNode : NodeBase
{
    private string _source = string.Empty;
    private string _destination = string.Empty;
    private ZipLevel _level;
    private ExistingFileMode _ifExists;

    private readonly InputPort _sourceIn;
    private readonly InputPort _destinationIn;
    private readonly OutputPort _successOut;
    private readonly OutputPort _savedAsOut;
    private readonly OutputPort _countOut;

    public ZipCreateNode()
    {
        // "any": one path as text, or a text list of files.
        _sourceIn = AddInput("Source", PortTypeCompatibility.Any);
        _destinationIn = AddInput<string>("Destination");
        _successOut = AddOutput<bool>("Success");
        _savedAsOut = AddOutput<string>("Saved As");
        _countOut = AddOutput<double>("Entries");

        AddRunGate();
    }

    /// <summary>A folder or a file.</summary>
    public string Source { get => _source; set => Set(ref _source, value ?? string.Empty); }

    /// <summary>The .zip to write; the extension is added when missing.</summary>
    public string Destination { get => _destination; set => Set(ref _destination, value ?? string.Empty); }

    public ZipLevel Level { get => _level; set => Set(ref _level, value); }

    public ExistingFileMode IfExists { get => _ifExists; set => Set(ref _ifExists, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        _successOut.Value = false;

        var sources = (_sourceIn.IsConnected ? NodeValues.Items(_sourceIn.Value).Select(NodeValues.ToText) : [Source])
            .Select(PathText.Clean)
            .Where(p => p.Length > 0)
            .ToList();
        if (sources.Count == 0)
            throw new InvalidOperationException("No source set.");

        var destination = PathText.Clean(_destinationIn.ValueOr(Destination));
        if (destination.Length == 0)
            throw new InvalidOperationException("No destination set.");
        if (System.IO.Path.GetExtension(destination).Length == 0)
            destination += ".zip";

        if (File.Exists(destination))
        {
            switch (IfExists)
            {
                case ExistingFileMode.Skip:
                    _savedAsOut.Value = destination;
                    return;
                case ExistingFileMode.AddNumber:
                    destination = FilePaths.NextFreeName(destination);
                    break;
            }
        }

        var level = Level switch
        {
            ZipLevel.Fastest => CompressionLevel.Fastest,
            ZipLevel.SmallestSize => CompressionLevel.SmallestSize,
            ZipLevel.NoCompression => CompressionLevel.NoCompression,
            _ => CompressionLevel.Optimal,
        };

        FilePaths.EnsureDirectory(destination);
        var token = context.CancellationToken;
        var count = await Task.Run(() => Write(sources, destination, level, token), token).ConfigureAwait(false);

        _successOut.Value = true;
        _savedAsOut.Value = destination;
        _countOut.Value = (double)count;
    }

    private static int Write(List<string> sources, string destination, CompressionLevel level, CancellationToken token)
    {
        // Written beside the target and moved into place, so a failure leaves no half-written zip.
        var partial = destination + ".partial";
        try
        {
            var count = 0;
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in sources)
                {
                    if (Directory.Exists(source))
                    {
                        foreach (var file in Directory.EnumerateFiles(source, "*", DirectoryPaths.Options(recursive: true)))
                        {
                            token.ThrowIfCancellationRequested();
                            Add(System.IO.Path.GetRelativePath(source, file), file);
                        }
                    }
                    else if (File.Exists(source))
                    {
                        Add(System.IO.Path.GetFileName(source), source);
                    }
                    else
                    {
                        throw new FileNotFoundException($"Not found: {source}");
                    }
                }

                void Add(string name, string file)
                {
                    name = name.Replace('\\', '/');
                    if (!names.Add(name))
                        throw new InvalidOperationException($"Two files would both be stored as {name}.");

                    zip.CreateEntryFromFile(file, name, level);
                    count++;
                }
            }

            File.Move(partial, destination, overwrite: true);
            return count;
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }
}

/// <summary>
/// Extracts a .zip into a folder. An entry that would land outside it ("../../evil", the "zip slip")
/// fails the node before anything of it is written, so an untrusted archive cannot reach elsewhere.
/// </summary>
[NodeDefinition("IO/Zip Extract", Description = "Extracts a .zip into a folder and lists the files it wrote.")]
public sealed class ZipExtractNode : NodeBase
{
    private string _path = string.Empty;
    private string _destination = string.Empty;
    private bool _overwrite;

    private readonly InputPort _pathIn;
    private readonly InputPort _destinationIn;
    private readonly OutputPort _filesOut;
    private readonly OutputPort _countOut;

    public ZipExtractNode()
    {
        _pathIn = AddInput<string>("Path");
        _destinationIn = AddInput<string>("Destination");
        _filesOut = AddOutput<string[]>("Files");
        _countOut = AddOutput<double>("Count");

        AddRunGate();
    }

    public string Path { get => _path; set => Set(ref _path, value ?? string.Empty); }

    /// <summary>The folder to extract into; empty means beside the zip, in a folder named after it.</summary>
    public string Destination { get => _destination; set => Set(ref _destination, value ?? string.Empty); }

    /// <summary>Replace files that are already there; off, an existing file fails the node.</summary>
    public bool Overwrite { get => _overwrite; set => Set(ref _overwrite, value); }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var path = FilePaths.Existing(_pathIn.IsConnected ? _pathIn.ValueOr(string.Empty) : Path);
        var destination = PathText.Clean(_destinationIn.ValueOr(Destination));
        if (destination.Length == 0)
            destination = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? string.Empty,
                System.IO.Path.GetFileNameWithoutExtension(path));

        var overwrite = Overwrite;
        var token = context.CancellationToken;
        var files = await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(path);
            var root = System.IO.Path.GetFullPath(destination).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;

            // Every entry is checked before any is written, so a hostile archive writes nothing at all.
            var entries = zip.Entries
                .Select(entry => (Entry: entry, Target: System.IO.Path.GetFullPath(System.IO.Path.Combine(root, entry.FullName))))
                .ToList();
            if (entries.FirstOrDefault(e => !e.Target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) is { Entry: not null } escaping)
                throw new InvalidOperationException($"{escaping.Entry.FullName} would extract outside the destination; nothing was extracted.");

            Directory.CreateDirectory(root);
            var written = new List<string>();
            foreach (var (entry, target) in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite);
                written.Add(target);
            }

            return written.ToArray();
        }, token).ConfigureAwait(false);

        _filesOut.Value = files;
        _countOut.Value = (double)files.Length;
    }
}

/// <summary>Folders offered by <see cref="SpecialFolderNode"/>. Saved by number: append, never reorder.</summary>
public enum SpecialFolderKind
{
    Temp,
    Desktop,
    Documents,
    Downloads,
    Pictures,
    Music,
    Videos,
    UserProfile,
    AppData,
    LocalAppData,
    ProgramData,
    ProgramFiles,
    CurrentDirectory,

    /// <summary>The folder vNoder itself runs from.</summary>
    AppDirectory,
}

[NodeDefinition("Path/Special Folder", Description = "The path of a well-known folder: Temp, Desktop, Documents, Downloads, AppData and others.")]
public sealed class SpecialFolderNode : NodeBase
{
    private SpecialFolderKind _folder;

    private readonly OutputPort _out;

    public SpecialFolderNode() => _out = AddOutput<string>("Path");

    public SpecialFolderKind Folder { get => _folder; set => Set(ref _folder, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _out.Value = Folder switch
        {
            SpecialFolderKind.Temp => System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar),
            SpecialFolderKind.Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            SpecialFolderKind.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),

            // .NET has no SpecialFolder for Downloads; this is where it lives unless moved by hand.
            SpecialFolderKind.Downloads => System.IO.Path.Combine(profile, "Downloads"),
            SpecialFolderKind.Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            SpecialFolderKind.Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            SpecialFolderKind.Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            SpecialFolderKind.AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            SpecialFolderKind.LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SpecialFolderKind.ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            SpecialFolderKind.ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            SpecialFolderKind.CurrentDirectory => Environment.CurrentDirectory,
            SpecialFolderKind.AppDirectory => AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar),
            _ => profile,
        };

        return Task.CompletedTask;
    }
}

/// <summary>
/// Size and free space of the drive a path is on. A drive that is not ready (an empty card reader, a
/// disconnected network share) is a result, Ready = false, not an error.
/// </summary>
[NodeDefinition("IO/Drive Info", Description = "Total and free space, format and label of the drive a path is on.")]
public sealed class DriveInfoNode : NodeBase
{
    private string _path = "C:\\";

    private readonly InputPort _pathIn;
    private readonly OutputPort _totalOut;
    private readonly OutputPort _freeOut;
    private readonly OutputPort _freePercentOut;
    private readonly OutputPort _formatOut;
    private readonly OutputPort _labelOut;
    private readonly OutputPort _readyOut;

    public DriveInfoNode()
    {
        _pathIn = AddInput<string>("Path");
        _totalOut = AddOutput<double>("Total");
        _freeOut = AddOutput<double>("Free");
        _freePercentOut = AddOutput<double>("Free %");
        _formatOut = AddOutput<string>("Format");
        _labelOut = AddOutput<string>("Label");
        _readyOut = AddOutput<bool>("Ready");

        AddRunGate();
    }

    /// <summary>A drive (C:\) or any path on it.</summary>
    public string Path { get => _path; set => Set(ref _path, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var path = PathText.Clean(_pathIn.ValueOr(Path));
        if (path.Length == 0)
            throw new InvalidOperationException("No path set.");

        var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException($"'{path}' has no drive.");

        var drive = new DriveInfo(root);
        _readyOut.Value = drive.IsReady;
        if (!drive.IsReady)
        {
            _totalOut.Value = _freeOut.Value = _freePercentOut.Value = _formatOut.Value = _labelOut.Value = null;
            return Task.CompletedTask;
        }

        // Free to this user, which is what a write will actually find, not the drive's raw free space.
        _totalOut.Value = (double)drive.TotalSize;
        _freeOut.Value = (double)drive.AvailableFreeSpace;
        _freePercentOut.Value = drive.TotalSize == 0 ? 0d : 100d * drive.AvailableFreeSpace / drive.TotalSize;
        _formatOut.Value = drive.DriveFormat;
        _labelOut.Value = drive.VolumeLabel;
        return Task.CompletedTask;
    }
}
