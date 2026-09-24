using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>What <see cref="DateTimeNode"/> produces.</summary>
public enum DateTimeSource
{
    /// <summary>The current local time.</summary>
    Now,

    /// <summary>The current time in UTC.</summary>
    UtcNow,

    /// <summary>Local midnight today.</summary>
    Today,
}

[NodeDefinition("Generators/Date Time", Description = "The current date and time, local or UTC, shifted by an offset.")]
public sealed class DateTimeNode : NodeBase
{
    private DateTimeSource _source;
    private double _offsetSeconds;

    private readonly InputPort _offsetIn;
    private readonly OutputPort _valueOut;
    private readonly OutputPort _unixOut;
    private readonly OutputPort _isoOut;

    public DateTimeNode()
    {
        _offsetIn = AddInput<double>("Offset");
        _valueOut = AddOutput<DateTime>("Date Time");
        _unixOut = AddOutput<double>("Unix Seconds");
        _isoOut = AddOutput<string>("ISO 8601");
    }

    public DateTimeSource Source
    {
        get => _source;
        set => Set(ref _source, value);
    }

    /// <summary>Seconds added to the result; negative for the past.</summary>
    public double OffsetSeconds
    {
        get => _offsetSeconds;
        set => Set(ref _offsetSeconds, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var value = Source switch
        {
            DateTimeSource.UtcNow => DateTime.UtcNow,
            DateTimeSource.Today => DateTime.Today,
            _ => DateTime.Now,
        };

        // Clamped to what DateTime can represent, so a wild offset saturates instead of throwing.
        var offset = _offsetIn.ValueOr(OffsetSeconds);
        value = DateMath.AddTicks(value, (double.IsNaN(offset) ? 0 : offset) * TimeSpan.TicksPerSecond);

        _valueOut.Value = value;
        // ToUniversalTime saturates at the limits, where DateTimeOffset would throw.
        _unixOut.Value = (value.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
        _isoOut.Value = value.ToString("o", CultureInfo.InvariantCulture);
        return Task.CompletedTask;
    }
}
