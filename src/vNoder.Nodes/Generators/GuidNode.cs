using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>How <see cref="GuidNode"/> prints its value; the names follow <see cref="Guid.ToString(string)"/>.</summary>
public enum GuidFormat
{
    /// <summary>00000000-0000-0000-0000-000000000000</summary>
    Hyphens,

    /// <summary>00000000000000000000000000000000</summary>
    Digits,

    /// <summary>{00000000-0000-0000-0000-000000000000}</summary>
    Braces,

    /// <summary>(00000000-0000-0000-0000-000000000000)</summary>
    Parentheses,
}

/// <summary>Which GUID to generate.</summary>
public enum GuidVersion
{
    /// <summary>Random.</summary>
    V4,

    /// <summary>Time-ordered: sorts by creation time, which databases index well.</summary>
    V7,
}

[NodeDefinition("Generators/GUID", Description = "A new GUID (v4 random or v7 time-ordered) each pass.")]
public sealed class GuidNode : NodeBase
{
    private GuidFormat _format;
    private GuidVersion _version;
    private bool _uppercase;

    private readonly OutputPort _out;

    public GuidNode() => _out = AddOutput<string>("GUID");

    public GuidFormat Format
    {
        get => _format;
        set => Set(ref _format, value);
    }

    public GuidVersion Version
    {
        get => _version;
        set => Set(ref _version, value);
    }

    public bool Uppercase
    {
        get => _uppercase;
        set => Set(ref _uppercase, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var guid = Version == GuidVersion.V7 ? Guid.CreateVersion7() : Guid.NewGuid();
        var text = guid.ToString(Format switch
        {
            GuidFormat.Digits => "N",
            GuidFormat.Braces => "B",
            GuidFormat.Parentheses => "P",
            _ => "D",
        });

        _out.Value = Uppercase ? text.ToUpperInvariant() : text;
        return Task.CompletedTask;
    }
}
