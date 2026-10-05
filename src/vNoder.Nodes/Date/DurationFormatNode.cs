using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>How <see cref="DurationFormatNode"/> writes a duration. Saved by number: append only.</summary>
public enum DurationStyle
{
    /// <summary>2d 3h 4m</summary>
    Compact,

    /// <summary>2 days, 3 hours, 4 minutes</summary>
    Long,

    /// <summary>02:03:04, or 2.03:04:05 with days</summary>
    Clock,

    /// <summary>P2DT3H4M (ISO 8601), for APIs and files</summary>
    Iso8601,
}

/// <summary>
/// A length of time for people to read: Difference, Uptime Hours or a timer's seconds as "2h 3m"
/// instead of 7384. The value is read in the chosen unit (seconds by default) and broken into days,
/// hours, minutes and seconds; months and years are not used, since their length varies.
/// </summary>
/// <remarks>
/// Compact and Long show at most Max Parts of the largest non-zero units, rounding the last one, so
/// 3725 seconds reads "1h 2m" with two parts. Under a second is written in milliseconds. A negative
/// duration gets a minus sign. Clock and ISO 8601 always show the whole duration.
/// </remarks>
[NodeDefinition("Date/Duration Format", Description = "A duration in seconds (or another unit) as text: 2h 3m, 2 hours 3 minutes, 02:03:00 or PT2H3M.")]
public sealed class DurationFormatNode : NodeBase
{
    private DateUnit _unit = DateUnit.Seconds;
    private DurationStyle _style;
    private double _maxParts = 2;

    private readonly InputPort _valueIn;
    private readonly OutputPort _out;

    private static readonly (string Short, string Singular, string Plural, long Seconds)[] Units =
    [
        ("d", "day", "days", 86_400),
        ("h", "hour", "hours", 3_600),
        ("m", "minute", "minutes", 60),
        ("s", "second", "seconds", 1),
    ];

    public DurationFormatNode()
    {
        _valueIn = AddInput<double>("Value");
        _out = AddOutput<string>("Text");
    }

    public DateUnit Unit { get => _unit; set => Set(ref _unit, value); }

    public DurationStyle Style { get => _style; set => Set(ref _style, value); }

    /// <summary>How many units Compact and Long show, from the largest; 1 to 4.</summary>
    public double MaxParts { get => _maxParts; set => Set(ref _maxParts, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var value = _valueIn.ValueOr(0d);
        var seconds = double.IsNaN(value) || double.IsInfinity(value)
            ? 0
            : Math.Clamp(value * DateMath.TicksPer(Unit) / TimeSpan.TicksPerSecond, -1e12, 1e12);

        var parts = double.IsNaN(MaxParts) ? 4 : (int)Math.Clamp(MaxParts, 1, 4);
        var sign = seconds < 0 ? "-" : string.Empty;
        seconds = Math.Abs(seconds);

        _out.Value = Style switch
        {
            DurationStyle.Clock => sign + Clock(seconds),
            DurationStyle.Iso8601 => sign + Iso(seconds),
            _ => sign + Words(seconds, parts, Style == DurationStyle.Long),
        };
        return Task.CompletedTask;
    }

    private static string Words(double seconds, int maxParts, bool longForm)
    {
        if (seconds < 1)
        {
            var ms = Math.Round(seconds * 1000);
            return longForm ? $"{ms} {(ms == 1 ? "millisecond" : "milliseconds")}" : $"{ms}ms";
        }

        // Find the largest unit present, then round the whole duration to the smallest unit shown, so
        // 1h 59m 40s with two parts reads 2h, not 1h 59m.
        var first = Array.FindIndex(Units, u => seconds >= u.Seconds);
        var last = Math.Min(first + maxParts, Units.Length) - 1;
        var total = (long)Math.Round(seconds / Units[last].Seconds) * Units[last].Seconds;
        first = Math.Max(0, Array.FindIndex(Units, u => total >= u.Seconds));
        last = Math.Min(first + maxParts, Units.Length) - 1;

        var pieces = new List<string>();
        for (var i = first; i <= last; i++)
        {
            var amount = total / Units[i].Seconds;
            total %= Units[i].Seconds;
            if (amount == 0)
                continue;

            pieces.Add(longForm
                ? $"{amount} {(amount == 1 ? Units[i].Singular : Units[i].Plural)}"
                : $"{amount}{Units[i].Short}");
        }

        return pieces.Count == 0 ? (longForm ? "0 seconds" : "0s") : string.Join(longForm ? ", " : " ", pieces);
    }

    private static string Clock(double seconds)
    {
        var total = (long)Math.Round(seconds);
        var days = total / 86_400;
        var clock = string.Create(CultureInfo.InvariantCulture, $"{total % 86_400 / 3_600:00}:{total % 3_600 / 60:00}:{total % 60:00}");
        return days > 0 ? $"{days}.{clock}" : clock;
    }

    private static string Iso(double seconds)
    {
        var total = (long)Math.Round(seconds);
        if (total == 0)
            return "PT0S";

        var days = total / 86_400;
        var time = (H: total % 86_400 / 3_600, M: total % 3_600 / 60, S: total % 60);
        var text = "P" + (days > 0 ? $"{days}D" : string.Empty);
        if (time != (0, 0, 0))
            text += "T" + (time.H > 0 ? $"{time.H}H" : "") + (time.M > 0 ? $"{time.M}M" : "") + (time.S > 0 ? $"{time.S}S" : "");

        return text;
    }
}
