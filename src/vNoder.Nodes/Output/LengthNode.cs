using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Shows how big its input is, and passes the count on as a number so it can feed arithmetic.
/// </summary>
[NodeDefinition("Output/Length", Description = "Counts the characters or bytes of its input.")]
public sealed class LengthNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public LengthNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _out = AddOutput<double>("Length");
    }

    /// <summary>Last measured size. Assigned during evaluation without notifying, like OutputNode.</summary>
    public double Count { get; private set; }

    public string DisplayText { get; private set; } = "—";

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        (Count, DisplayText) = _in.Value switch
        {
            null => (0d, "—"),
            string text => (text.Length, Unit(text.Length, "character")),
            byte[] bytes => (bytes.Length, Unit(bytes.Length, "byte")),
            Array list => (list.Length, Unit(list.Length, "item")),
            bool flag => (flag ? 1d : 0d, flag ? "true" : "false"),
            double number => Measure(number.ToString("0.######", CultureInfo.InvariantCulture)),
            DateTime moment => Measure(moment.ToString("o", CultureInfo.InvariantCulture)),
            var other => Measure(other.ToString() ?? string.Empty),
        };

        _out.Value = Count;
        return Task.CompletedTask;
    }

    /// <summary>Run per item, it shows every item's length, one per line, not just the last one's.</summary>
    protected override void ItemsGathered() => DisplayText = NodeValues.ToText(_out.Value);

    // Anything without a natural size is measured by how it prints.
    private static (double, string) Measure(string text) => (text.Length, Unit(text.Length, "character"));

    private static string Unit(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
