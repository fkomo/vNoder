using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Value equality across whatever two ports carry. Deliberately not string-only: comparing two
/// numbers or two buffers is the same question.
/// </summary>
[NodeDefinition("Logic/Equals", Description = "True when both inputs hold the same value.")]
public sealed class EqualsNode : NodeBase
{
    private bool _ignoreCase;

    private readonly InputPort _a;
    private readonly InputPort _b;
    private readonly OutputPort _out;

    public EqualsNode()
    {
        _a = AddInput("A", PortTypeCompatibility.Any);
        _b = AddInput("B", PortTypeCompatibility.Any);
        _out = AddOutput<bool>("Result");
    }

    /// <summary>Applies only when the comparison falls back to text.</summary>
    public bool IgnoreCase
    {
        get => _ignoreCase;
        set => Set(ref _ignoreCase, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = AreEqual(_a.Value, _b.Value, IgnoreCase);
        return Task.CompletedTask;
    }

    internal static bool AreEqual(object? left, object? right, bool ignoreCase) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (double a, double b) => a.Equals(b),
        (bool a, bool b) => a == b,
        (DateTime a, DateTime b) => a == b,
        (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
        (string[] a, string[] b) => a.SequenceEqual(b, Comparer(ignoreCase)),
        _ => string.Equals(Text(left), Text(right), ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal),
    };

    private static StringComparer Comparer(bool ignoreCase) =>
        ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Text(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        double number => number.ToString("0.######", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        string[] parts => string.Join('\u001f', parts),
        DateTime moment => moment.ToString("o", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
