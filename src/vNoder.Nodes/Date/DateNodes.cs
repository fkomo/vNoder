using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

// Date arithmetic and conversion. Like Format, an unwired date input means "now", so each node is
// useful on its own. Arithmetic saturates at DateTime's range rather than throwing (see DateMath).

public enum DateUnit
{
    Seconds,
    Minutes,
    Hours,
    Days,
    Weeks,

    /// <summary>Calendar months when adding; an average month (30.44 days) when measuring.</summary>
    Months,

    /// <summary>Calendar years when adding; an average year (365.2425 days) when measuring.</summary>
    Years,
    Milliseconds,
}

[NodeDefinition("Date/Parse", Description = "Reads a date and time from text: ISO 8601 and common forms, or an exact .NET format.")]
public sealed class ParseDateNode : NodeBase
{
    private string _format = string.Empty;
    private string _culture = string.Empty;

    private readonly InputPort _textIn;
    private readonly InputPort _formatIn;
    private readonly OutputPort _out;
    private readonly OutputPort _successOut;

    public ParseDateNode()
    {
        _textIn = AddInput<string>("Text");
        _formatIn = AddInput<string>("Format");
        _out = AddOutput<DateTime>("Date Time");
        _successOut = AddOutput<bool>("Success");
    }

    /// <summary>Exact format such as dd.MM.yyyy; empty accepts ISO 8601 and the culture's usual forms.</summary>
    public string Format
    {
        get => _format;
        set => Set(ref _format, value ?? string.Empty);
    }

    /// <summary>Culture for month names and day/month order, e.g. "en-US"; empty means invariant.</summary>
    public string Culture
    {
        get => _culture;
        set => Set(ref _culture, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _textIn.ValueOr(string.Empty).Trim();
        var format = _formatIn.ValueOr(Format);
        var culture = NodeValues.Culture(Culture);

        // RoundtripKind keeps a trailing Z (or offset) meaningful: "…Z" parses as UTC, not local.
        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind;
        var parsed = string.IsNullOrEmpty(format)
            ? DateTime.TryParse(text, culture, styles, out var value)
            : DateTime.TryParseExact(text, format, culture, styles, out value);

        // Unparseable text is a result, as it is for To Number, so it can drive a Select.
        _out.Value = parsed ? value : null;
        _successOut.Value = parsed;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Date/Add", Description = "Adds an amount of seconds, minutes, hours, days, weeks, months or years to a date; negative subtracts.")]
public sealed class AddToDateNode : NodeBase
{
    private double _amount = 1;
    private DateUnit _unit = DateUnit.Days;

    private readonly InputPort _dateIn;
    private readonly InputPort _amountIn;
    private readonly OutputPort _out;

    public AddToDateNode()
    {
        _dateIn = AddInput<DateTime>("Date Time");
        _amountIn = AddInput<double>("Amount");
        _out = AddOutput<DateTime>("Date Time");
    }

    public double Amount
    {
        get => _amount;
        set => Set(ref _amount, value);
    }

    public DateUnit Unit
    {
        get => _unit;
        set => Set(ref _unit, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var date = _dateIn.IsConnected ? _dateIn.ValueOr(DateTime.Now) : DateTime.Now;
        var amount = _amountIn.ValueOr(Amount);
        _out.Value = double.IsNaN(amount) ? date : DateMath.Add(date, amount, Unit);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Date/Difference", Description = "How long from A to B (B − A), in a chosen unit; negative when B is earlier.")]
public sealed class DateDifferenceNode : NodeBase
{
    private DateUnit _unit = DateUnit.Days;

    private readonly InputPort _aIn;
    private readonly InputPort _bIn;
    private readonly OutputPort _out;

    public DateDifferenceNode()
    {
        _aIn = AddInput<DateTime>("A");
        _bIn = AddInput<DateTime>("B");
        _out = AddOutput<double>("Difference");
    }

    public DateUnit Unit
    {
        get => _unit;
        set => Set(ref _unit, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var now = DateTime.Now;
        var a = _aIn.IsConnected ? _aIn.ValueOr(now) : now;
        var b = _bIn.IsConnected ? _bIn.ValueOr(now) : now;

        // A UTC date against a local one would otherwise be off by the time-zone offset.
        if (a.Kind != b.Kind)
        {
            a = a.ToUniversalTime();
            b = b.ToUniversalTime();
        }

        _out.Value = (b - a).Ticks / DateMath.TicksPer(Unit);
        return Task.CompletedTask;
    }
}

[NodeDefinition("Date/To Unix", Description = "A date as a Unix timestamp, in seconds and milliseconds since 1970-01-01 UTC.")]
public sealed class ToUnixNode : NodeBase
{
    private readonly InputPort _dateIn;
    private readonly OutputPort _secondsOut;
    private readonly OutputPort _millisecondsOut;

    public ToUnixNode()
    {
        _dateIn = AddInput<DateTime>("Date Time");
        _secondsOut = AddOutput<double>("Seconds");
        _millisecondsOut = AddOutput<double>("Milliseconds");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var date = _dateIn.IsConnected ? _dateIn.ValueOr(DateTime.UtcNow) : DateTime.UtcNow;

        // An Unspecified date (parsed without a zone) is read as local, as ToUniversalTime does.
        var elapsed = date.ToUniversalTime() - DateTime.UnixEpoch;
        _secondsOut.Value = Math.Floor(elapsed.TotalSeconds);
        _millisecondsOut.Value = Math.Floor(elapsed.TotalMilliseconds);
        return Task.CompletedTask;
    }
}

public enum UnixUnit
{
    Seconds,
    Milliseconds,
}

[NodeDefinition("Date/From Unix", Description = "A date from a Unix timestamp in seconds or milliseconds.")]
public sealed class FromUnixNode : NodeBase
{
    private double _timestamp;
    private UnixUnit _unit;
    private bool _utc;

    private readonly InputPort _timestampIn;
    private readonly OutputPort _out;

    public FromUnixNode()
    {
        _timestampIn = AddInput<double>("Timestamp");
        _out = AddOutput<DateTime>("Date Time");
    }

    public double Timestamp
    {
        get => _timestamp;
        set => Set(ref _timestamp, value);
    }

    public UnixUnit Unit
    {
        get => _unit;
        set => Set(ref _unit, value);
    }

    /// <summary>Give the result in UTC rather than local time.</summary>
    public bool Utc
    {
        get => _utc;
        set => Set(ref _utc, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var timestamp = _timestampIn.ValueOr(Timestamp);
        var ticks = double.IsNaN(timestamp)
            ? 0
            : timestamp * (Unit == UnixUnit.Milliseconds ? TimeSpan.TicksPerMillisecond : TimeSpan.TicksPerSecond);

        var utc = DateMath.AddTicks(DateTime.UnixEpoch, ticks);
        _out.Value = Utc ? utc : utc.ToLocalTime();
        return Task.CompletedTask;
    }
}

internal static class DateMath
{
    public static double TicksPer(DateUnit unit) => unit switch
    {
        DateUnit.Milliseconds => TimeSpan.TicksPerMillisecond,
        DateUnit.Minutes => TimeSpan.TicksPerMinute,
        DateUnit.Hours => TimeSpan.TicksPerHour,
        DateUnit.Days => TimeSpan.TicksPerDay,
        DateUnit.Weeks => TimeSpan.TicksPerDay * 7d,
        DateUnit.Months => TimeSpan.TicksPerDay * 365.2425 / 12,
        DateUnit.Years => TimeSpan.TicksPerDay * 365.2425,
        _ => TimeSpan.TicksPerSecond,
    };

    /// <summary>
    /// Adds in calendar terms for months and years (Jan 31 + 1 month is Feb 28/29), exact durations for
    /// the rest. A fractional month or year adds its whole part by the calendar and the rest as an
    /// average length.
    /// </summary>
    public static DateTime Add(DateTime date, double amount, DateUnit unit)
    {
        if (unit is DateUnit.Months or DateUnit.Years)
        {
            var months = unit == DateUnit.Years ? amount * 12 : amount;
            var whole = Math.Truncate(months);

            // DateTime spans under 120,000 months, so anything larger saturates without trying.
            if (Math.Abs(whole) > 120_000)
                return whole > 0 ? new DateTime(DateTime.MaxValue.Ticks, date.Kind) : new DateTime(DateTime.MinValue.Ticks, date.Kind);

            try
            {
                date = date.AddMonths((int)whole);
            }
            catch (ArgumentOutOfRangeException)
            {
                return whole > 0 ? new DateTime(DateTime.MaxValue.Ticks, date.Kind) : new DateTime(DateTime.MinValue.Ticks, date.Kind);
            }

            return AddTicks(date, (months - whole) * TicksPer(DateUnit.Months));
        }

        return AddTicks(date, amount * TicksPer(unit));
    }

    /// <summary>
    /// Adds ticks, saturating at DateTime's range. Only the offset passes through double, and it is
    /// rounded to whole ticks before being added as a long: a date's own tick count (~6e17) is past
    /// double's exact range, so summing as doubles lost milliseconds, and the limits do not survive a
    /// round trip through double either (MaxValue.Ticks rounds up past itself).
    /// </summary>
    public static DateTime AddTicks(DateTime date, double ticks)
    {
        if (double.IsNaN(ticks))
            return date;

        var up = DateTime.MaxValue.Ticks - date.Ticks;
        var down = DateTime.MinValue.Ticks - date.Ticks;

        // Anything beyond the whole range saturates before the conversion, which could overflow.
        if (ticks >= DateTime.MaxValue.Ticks)
            return new DateTime(DateTime.MaxValue.Ticks, date.Kind);
        if (ticks <= -DateTime.MaxValue.Ticks)
            return new DateTime(DateTime.MinValue.Ticks, date.Kind);

        var whole = (long)Math.Round(ticks);
        return new DateTime(
            whole >= up ? DateTime.MaxValue.Ticks
            : whole <= down ? DateTime.MinValue.Ticks
            : date.Ticks + whole,
            date.Kind);
    }
}
