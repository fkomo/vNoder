using System.Globalization;
using Cronos;
using vNoder.Core;

namespace vNoder.Nodes;

// Like the other Date nodes, an unwired date input means "now".

[NodeDefinition("Date/Date Parts", Description = "Splits a date into year, month, day, hour, minute, second, weekday, day of year and ISO week.")]
public sealed class DatePartsNode : NodeBase
{
    private string _culture = string.Empty;

    private readonly InputPort _dateIn;
    private readonly OutputPort _yearOut;
    private readonly OutputPort _monthOut;
    private readonly OutputPort _dayOut;
    private readonly OutputPort _hourOut;
    private readonly OutputPort _minuteOut;
    private readonly OutputPort _secondOut;
    private readonly OutputPort _millisecondOut;
    private readonly OutputPort _weekdayOut;
    private readonly OutputPort _dayNameOut;
    private readonly OutputPort _dayOfYearOut;
    private readonly OutputPort _weekOut;

    public DatePartsNode()
    {
        _dateIn = AddInput<DateTime>("Date Time");
        _yearOut = AddOutput<double>("Year");
        _monthOut = AddOutput<double>("Month");
        _dayOut = AddOutput<double>("Day");
        _hourOut = AddOutput<double>("Hour");
        _minuteOut = AddOutput<double>("Minute");
        _secondOut = AddOutput<double>("Second");
        _millisecondOut = AddOutput<double>("Millisecond");
        _weekdayOut = AddOutput<double>("Weekday");
        _dayNameOut = AddOutput<string>("Day Name");
        _dayOfYearOut = AddOutput<double>("Day Of Year");
        _weekOut = AddOutput<double>("ISO Week");
    }

    /// <summary>Culture for Day Name, e.g. "sk-SK"; empty means English.</summary>
    public string Culture { get => _culture; set => Set(ref _culture, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var date = _dateIn.IsConnected ? _dateIn.ValueOr(DateTime.Now) : DateTime.Now;

        _yearOut.Value = (double)date.Year;
        _monthOut.Value = (double)date.Month;
        _dayOut.Value = (double)date.Day;
        _hourOut.Value = (double)date.Hour;
        _minuteOut.Value = (double)date.Minute;
        _secondOut.Value = (double)date.Second;
        _millisecondOut.Value = (double)date.Millisecond;

        // ISO numbering: Monday is 1, Sunday 7.
        _weekdayOut.Value = date.DayOfWeek == DayOfWeek.Sunday ? 7d : (double)date.DayOfWeek;
        _dayNameOut.Value = date.ToString("dddd", NodeValues.Culture(Culture));
        _dayOfYearOut.Value = (double)date.DayOfYear;
        _weekOut.Value = (double)ISOWeek.GetWeekOfYear(date);
        return Task.CompletedTask;
    }
}

/// <summary>Periods offered by <see cref="PeriodBoundsNode"/>. Saved by number: append, never reorder.</summary>
public enum DatePeriod
{
    Minute,
    Hour,
    Day,
    Week,
    Month,
    Quarter,
    Year,
}

/// <summary>
/// The first and last moment of the period a date falls in. End is the last tick before the next
/// period starts, so "between Start and End" covers the whole period. Weeks start on Monday (ISO)
/// unless Week Starts Sunday is ticked.
/// </summary>
[NodeDefinition("Date/Start End Of", Description = "The start and end of the minute, hour, day, week, month, quarter or year a date falls in.")]
public sealed class PeriodBoundsNode : NodeBase
{
    private DatePeriod _period = DatePeriod.Day;
    private bool _weekStartsSunday;

    private readonly InputPort _dateIn;
    private readonly OutputPort _startOut;
    private readonly OutputPort _endOut;

    public PeriodBoundsNode()
    {
        _dateIn = AddInput<DateTime>("Date Time");
        _startOut = AddOutput<DateTime>("Start");
        _endOut = AddOutput<DateTime>("End");
    }

    public DatePeriod Period { get => _period; set => Set(ref _period, value); }

    public bool WeekStartsSunday { get => _weekStartsSunday; set => Set(ref _weekStartsSunday, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var date = _dateIn.IsConnected ? _dateIn.ValueOr(DateTime.Now) : DateTime.Now;
        var kind = date.Kind;

        var start = Period switch
        {
            DatePeriod.Minute => new DateTime(date.Year, date.Month, date.Day, date.Hour, date.Minute, 0, kind),
            DatePeriod.Hour => new DateTime(date.Year, date.Month, date.Day, date.Hour, 0, 0, kind),
            DatePeriod.Week => date.Date.AddDays(-(((int)date.DayOfWeek - (WeekStartsSunday ? 0 : 1) + 7) % 7)),
            DatePeriod.Month => new DateTime(date.Year, date.Month, 1, 0, 0, 0, kind),
            DatePeriod.Quarter => new DateTime(date.Year, ((date.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, kind),
            DatePeriod.Year => new DateTime(date.Year, 1, 1, 0, 0, 0, kind),
            _ => date.Date,
        };

        // Saturates at DateTime's range, like the rest of the date arithmetic.
        var next = Period switch
        {
            DatePeriod.Minute => DateMath.Add(start, 1, DateUnit.Minutes),
            DatePeriod.Hour => DateMath.Add(start, 1, DateUnit.Hours),
            DatePeriod.Week => DateMath.Add(start, 1, DateUnit.Weeks),
            DatePeriod.Month => DateMath.Add(start, 1, DateUnit.Months),
            DatePeriod.Quarter => DateMath.Add(start, 3, DateUnit.Months),
            DatePeriod.Year => DateMath.Add(start, 1, DateUnit.Years),
            _ => DateMath.Add(start, 1, DateUnit.Days),
        };

        _startOut.Value = start;
        _endOut.Value = next == DateTime.MaxValue ? next : next.AddTicks(-1);
        return Task.CompletedTask;
    }
}

internal static class TimeZones
{
    /// <summary>
    /// A zone by IANA name ("Europe/Bratislava") or Windows id ("Central Europe Standard Time"), or the
    /// words Local and UTC. Null for an empty name, meaning "whatever the date says".
    /// </summary>
    public static TimeZoneInfo? Find(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return null;

        if (name.Equals("local", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Local;

        if (name.Equals("utc", StringComparison.OrdinalIgnoreCase) || name.Equals("gmt", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"Unknown time zone '{name}'. Use an IANA name like Europe/Bratislava, or Local or UTC.");
        }
    }
}

/// <summary>
/// Converts a wall-clock time from one zone to another, daylight saving included. With From Zone
/// empty, the date is read as it says it is: UTC if it is marked UTC (a parsed "…Z"), local otherwise.
/// </summary>
[NodeDefinition("Date/Time Zone Convert", Description = "Converts a date and time between time zones (IANA names like Europe/Bratislava, or Local and UTC).")]
public sealed class TimeZoneConvertNode : NodeBase
{
    private string _fromZone = string.Empty;
    private string _toZone = "UTC";

    private readonly InputPort _dateIn;
    private readonly OutputPort _out;
    private readonly OutputPort _offsetOut;

    public TimeZoneConvertNode()
    {
        _dateIn = AddInput<DateTime>("Date Time");
        _out = AddOutput<DateTime>("Date Time");
        _offsetOut = AddOutput<double>("Offset Hours");
    }

    public string FromZone { get => _fromZone; set => Set(ref _fromZone, value ?? string.Empty); }

    public string ToZone { get => _toZone; set => Set(ref _toZone, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var date = _dateIn.IsConnected ? _dateIn.ValueOr(DateTime.Now) : DateTime.Now;
        var from = TimeZones.Find(FromZone) ?? (date.Kind == DateTimeKind.Utc ? TimeZoneInfo.Utc : TimeZoneInfo.Local);
        var to = TimeZones.Find(ToZone) ?? TimeZoneInfo.Local;

        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), from);
        var converted = TimeZoneInfo.ConvertTimeFromUtc(utc, to);

        // Marked as what it is where .NET can say so, so a later conversion or To Unix reads it right.
        _out.Value = to == TimeZoneInfo.Utc ? DateTime.SpecifyKind(converted, DateTimeKind.Utc)
            : to == TimeZoneInfo.Local ? DateTime.SpecifyKind(converted, DateTimeKind.Local)
            : converted;
        _offsetOut.Value = to.GetUtcOffset(utc).TotalHours;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The next times a cron expression fires: five fields (minute hour day month weekday), six with
/// seconds first, or @hourly, @daily, @weekly, @monthly, @yearly. Read in a time zone (daylight saving
/// handled), results in local time.
/// </summary>
[NodeDefinition("Date/Cron Next", Description = "The next times a cron schedule (e.g. 0 9 * * MON-FRI) fires, from now or a given date.")]
public sealed class CronNextNode : NodeBase
{
    private string _expression = "0 9 * * MON-FRI";
    private double _count = 5;
    private string _timeZone = string.Empty;

    private readonly InputPort _expressionIn;
    private readonly InputPort _fromIn;
    private readonly InputPort _countIn;
    private readonly OutputPort _nextOut;
    private readonly OutputPort _upcomingOut;
    private readonly OutputPort _foundOut;

    public CronNextNode()
    {
        _expressionIn = AddInput<string>("Expression");
        _fromIn = AddInput<DateTime>("From");
        _countIn = AddInput<double>("Count");
        _nextOut = AddOutput<DateTime>("Next");
        _upcomingOut = AddOutput<DateTime[]>("Upcoming");
        _foundOut = AddOutput<bool>("Found");
    }

    public string Expression { get => _expression; set => Set(ref _expression, value ?? string.Empty); }

    /// <summary>How many upcoming times to list, 1 to 1000.</summary>
    public double Count { get => _count; set => Set(ref _count, value); }

    /// <summary>The zone the schedule is written in; empty means local.</summary>
    public string TimeZone { get => _timeZone; set => Set(ref _timeZone, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var cron = CronText.Parse(_expressionIn.ValueOr(Expression));

        var zone = TimeZones.Find(TimeZone) ?? TimeZoneInfo.Local;
        var from = (_fromIn.IsConnected ? _fromIn.ValueOr(DateTime.Now) : DateTime.Now).ToUniversalTime();
        var countValue = _countIn.ValueOr(Count);
        var count = double.IsNaN(countValue) ? 1 : (int)Math.Clamp(countValue, 1, 1000);

        var upcoming = new List<DateTime>(count);
        var cursor = from;
        while (upcoming.Count < count && cron.GetNextOccurrence(cursor, zone) is { } next)
        {
            upcoming.Add(next.ToLocalTime());
            cursor = next;
        }

        // A schedule that never fires again (Feb 30th) is a result, not an error.
        _upcomingOut.Value = upcoming.ToArray();
        _nextOut.Value = upcoming.Count > 0 ? upcoming[0] : null;
        _foundOut.Value = upcoming.Count > 0;
        return Task.CompletedTask;
    }
}

/// <summary>Cron parsing shared by Cron Next and Cron Due: five fields, or six with seconds first.</summary>
internal static class CronText
{
    public static CronExpression Parse(string? expression)
    {
        var text = (expression ?? string.Empty).Trim();
        try
        {
            var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return CronExpression.Parse(text, fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
        }
        catch (CronFormatException exception)
        {
            throw new InvalidOperationException($"Invalid cron expression: {exception.Message}");
        }
    }
}
