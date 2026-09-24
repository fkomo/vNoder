using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition(
    "Date/Format",
    Description = "Formats a date and time as text with a .NET format string, e.g. yyyy-MM-dd HH:mm:ss.",
    FormerPaths = ["Text/Format Date Time"])]
public sealed class FormatDateTimeNode : NodeBase
{
    private string _format = "yyyy-MM-dd HH:mm:ss";
    private string _culture = string.Empty;

    private readonly InputPort _valueIn;
    private readonly InputPort _formatIn;
    private readonly OutputPort _out;

    public FormatDateTimeNode()
    {
        _valueIn = AddInput<DateTime>("Date Time");
        _formatIn = AddInput<string>("Format");
        _out = AddOutput<string>("Text");
    }

    /// <summary>Standard ("o", "R", "d") or custom ("dd.MM.yyyy HH:mm") format string.</summary>
    public string Format
    {
        get => _format;
        set => Set(ref _format, value ?? string.Empty);
    }

    /// <summary>Culture name for month and day names, e.g. "de-DE"; empty means invariant.</summary>
    public string Culture
    {
        get => _culture;
        set => Set(ref _culture, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Unwired, format the current time: the node is useful on its own as a clock.
        var value = _valueIn.IsConnected ? _valueIn.ValueOr(DateTime.Now) : DateTime.Now;
        var format = _formatIn.ValueOr(Format);

        try
        {
            _out.Value = value.ToString(string.IsNullOrEmpty(format) ? null : format, NodeValues.Culture(Culture));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"'{format}' is not a valid date/time format.");
        }

        return Task.CompletedTask;
    }
}
