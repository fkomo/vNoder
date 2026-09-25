using System.Security.Cryptography;
using System.Text;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// A list of numbers, the natural source for a per-item run: "do this ten times", "pages 1 to 20".
/// </summary>
[NodeDefinition("Generators/Range", Description = "A list of numbers: Count of them, from Start, Step apart (e.g. 1, 2, 3 … 10).")]
public sealed class RangeNode : NodeBase
{
    private double _start;
    private double _count = 10;
    private double _step = 1;

    private readonly InputPort _startIn;
    private readonly InputPort _countIn;
    private readonly InputPort _stepIn;
    private readonly OutputPort _out;

    public RangeNode()
    {
        _startIn = AddInput<double>("Start");
        _countIn = AddInput<double>("Count");
        _stepIn = AddInput<double>("Step");
        _out = AddOutput<double[]>("Numbers");
    }

    public double Start { get => _start; set => Set(ref _start, value); }

    /// <summary>How many numbers, 0 to 1,000,000.</summary>
    public double Count { get => _count; set => Set(ref _count, value); }

    public double Step { get => _step; set => Set(ref _step, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var start = _startIn.ValueOr(Start);
        var step = _stepIn.ValueOr(Step);
        var count = _countIn.ValueOr(Count);
        var n = double.IsNaN(count) ? 0 : (int)Math.Clamp(count, 0, 1_000_000);

        // Multiplied rather than accumulated, so 0.1 steps do not drift.
        var numbers = new double[n];
        for (var i = 0; i < n; i++)
            numbers[i] = start + (i * step);

        _out.Value = numbers;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Generators/Random Gaussian", Description = "A normally distributed random number (bell curve) with a mean and standard deviation.")]
public sealed class RandomGaussianNode : NodeBase
{
    private double _mean;
    private double _stdDev = 1;

    private readonly InputPort _meanIn;
    private readonly InputPort _stdDevIn;
    private readonly OutputPort _out;

    public RandomGaussianNode()
    {
        _meanIn = AddInput<double>("Mean");
        _stdDevIn = AddInput<double>("Std Dev");
        _out = AddOutput<double>("Value");
    }

    public double Mean { get => _mean; set => Set(ref _mean, value); }

    public double StdDev { get => _stdDev; set => Set(ref _stdDev, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Box–Muller; 1 - NextDouble() is in (0, 1], so the log never sees zero.
        var u1 = 1 - Random.Shared.NextDouble();
        var u2 = Random.Shared.NextDouble();
        var standard = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);

        _out.Value = _meanIn.ValueOr(Mean) + (standard * _stdDevIn.ValueOr(StdDev));
        return Task.CompletedTask;
    }
}

/// <summary>The namespaces RFC 4122 defines for name-based GUIDs, or a custom one.</summary>
public enum GuidNamespace
{
    DNS,
    URL,
    OID,
    X500,
    Custom,
}

/// <summary>Hash for <see cref="NameBasedGuidNode"/>.</summary>
public enum NameBasedGuidVersion
{
    /// <summary>SHA-1, the recommended one.</summary>
    V5,

    /// <summary>MD5, for compatibility with systems that use it.</summary>
    V3,
}

/// <summary>
/// A GUID derived from a name (RFC 4122 v5 or v3): the same namespace and name always give the same
/// GUID, so it can serve as a stable id for a URL, a file path or a record key.
/// </summary>
[NodeDefinition("Generators/Name-Based GUID", Description = "A deterministic GUID (v5 SHA-1 or v3 MD5) from a namespace and a name; the same name always gives the same GUID.")]
public sealed class NameBasedGuidNode : NodeBase
{
    private GuidNamespace _namespace;
    private string _customNamespace = string.Empty;
    private string _name = string.Empty;
    private NameBasedGuidVersion _version;

    private readonly InputPort _nameIn;
    private readonly OutputPort _out;

    public NameBasedGuidNode()
    {
        _nameIn = AddInput<string>("Name");
        _out = AddOutput<string>("GUID");
    }

    public GuidNamespace Namespace { get => _namespace; set => Set(ref _namespace, value); }

    /// <summary>The namespace GUID when <see cref="Namespace"/> is Custom.</summary>
    public string CustomNamespace { get => _customNamespace; set => Set(ref _customNamespace, value ?? string.Empty); }

    public string Name { get => _name; set => Set(ref _name, value ?? string.Empty); }

    public NameBasedGuidVersion Version { get => _version; set => Set(ref _version, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var ns = Namespace switch
        {
            GuidNamespace.URL => new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8"),
            GuidNamespace.OID => new Guid("6ba7b812-9dad-11d1-80b4-00c04fd430c8"),
            GuidNamespace.X500 => new Guid("6ba7b814-9dad-11d1-80b4-00c04fd430c8"),
            GuidNamespace.Custom => Guid.TryParse(CustomNamespace.Trim(), out var custom)
                ? custom
                : throw new InvalidOperationException($"'{CustomNamespace}' is not a GUID."),
            _ => new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"),
        };

        _out.Value = Create(ns, _nameIn.ValueOr(Name), Version).ToString("D");
        return Task.CompletedTask;
    }

    internal static Guid Create(Guid ns, string name, NameBasedGuidVersion version)
    {
        // RFC 4122 hashes the namespace in network byte order, which is not how .NET lays out a Guid.
        byte[] input = [.. ns.ToByteArray(bigEndian: true), .. Encoding.UTF8.GetBytes(name)];
        var hash = version == NameBasedGuidVersion.V3 ? MD5.HashData(input) : SHA1.HashData(input);

        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | (version == NameBasedGuidVersion.V3 ? 0x30 : 0x50));
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
