using System.Security.Cryptography;
using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Hash functions offered by <see cref="HashNode"/>.</summary>
public enum HashAlgorithmKind
{
    MD5,
    SHA1,
    SHA256,
    SHA384,
    SHA512,
}

[NodeDefinition("Crypto/Hash", Description = "Hashes text or bytes to a hex digest.")]
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
        _ => SHA256.HashData(bytes),
    };
}
