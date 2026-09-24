using System.Security.Cryptography;
using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Hash functions offered by <see cref="HashNode"/>. Saved by number: append, never reorder.</summary>
public enum HashAlgorithmKind
{
    MD5,
    SHA1,
    SHA256,
    SHA384,
    SHA512,

    /// <summary>A checksum rather than a cryptographic hash: detects accidents, not tampering.</summary>
    CRC32,
}

[NodeDefinition("Crypto/Hash", Description = "Hashes text or bytes to a hex digest: MD5, SHA-1, SHA-2 or a CRC32 checksum.")]
public sealed class HashNode : NodeBase
{
    private HashAlgorithmKind _algorithm = HashAlgorithmKind.SHA256;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public HashNode()
    {
        // Declared as "any" so the same node accepts text or bytes upstream; the runtime check below
        // is what rejects anything else.
        _in = AddInput("Data", PortTypeCompatibility.Any);
        _out = AddOutput<string>("Digest");
    }

    public HashAlgorithmKind Algorithm
    {
        get => _algorithm;
        set => Set(ref _algorithm, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var bytes = _in.Value switch
        {
            null => Array.Empty<byte>(),
            byte[] raw => raw,
            string text => Encoding.UTF8.GetBytes(text),
            var other => throw new InvalidOperationException(
                $"Hashing needs text or bytes, got {other.GetType().Name}."),
        };

        _out.Value = Convert.ToHexString(Compute(bytes)).ToLowerInvariant();
        return Task.CompletedTask;
    }

    private byte[] Compute(byte[] bytes) => Algorithm switch
    {
        HashAlgorithmKind.MD5 => MD5.HashData(bytes),
        HashAlgorithmKind.SHA1 => SHA1.HashData(bytes),
        HashAlgorithmKind.SHA384 => SHA384.HashData(bytes),
        HashAlgorithmKind.SHA512 => SHA512.HashData(bytes),
        HashAlgorithmKind.CRC32 => Crc32(bytes),
        _ => SHA256.HashData(bytes),
    };

    private static readonly uint[] Crc32Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

        return c;
    }).ToArray();

    /// <summary>
    /// CRC-32 as zip, gzip and PNG use it (IEEE 802.3, reflected), big-endian so the hex reads as the
    /// checksum is usually printed. Written out rather than taken from System.IO.Hashing, which is a
    /// package, not part of the BCL.
    /// </summary>
    private static byte[] Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

        crc ^= 0xFFFFFFFFu;
        return [(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }
}
