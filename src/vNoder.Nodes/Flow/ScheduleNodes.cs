using vNoder.Core;

namespace vNoder.Nodes;

// Pacing for continuous runs. A run ticks at the graph's interval (often well under a second), but
// "check the API every ten minutes" or "send the report at nine on weekdays" want a slower beat. These
// give a boolean that is true on the passes where the slower beat falls, to drive Run If.

/// <summary>Time units for <see cref="EveryNode"/>. Saved by number: append only.</summary>
public enum EveryUnit
{
    Seconds,
    Minutes,
    Hours,

    /// <summary>Every Nth pass of the run, whatever the time between them.</summary>
    Passes,
}

/// <summary>
/// True at most once per interval: on the first pass (unless First Pass is off), then on the first
/// pass after each interval has gone by. Into Run If, it makes a node act every ten minutes in a run
/// that ticks every half second. The interval is measured from the pass that fired, so a slow pass
/// delays the next one rather than making two fire together.
/// </summary>
[NodeDefinition("Flow/Every", Description = "True once per interval (seconds, minutes, hours or passes) — slows a continuous run down for one branch.")]
public sealed class EveryNode : StatefulNode
{
    private double _interval = 10;
    private EveryUnit _unit = EveryUnit.Minutes;
    private bool _firstPass = true;

    private DateTime? _lastFired;
    private long _passesSinceFired;
    private double _fired;

    private readonly InputPort _intervalIn;
    private readonly OutputPort _dueOut;
    private readonly OutputPort _countOut;

    public EveryNode()
    {
        _intervalIn = AddInput<double>("Interval");
        _dueOut = AddOutput<bool>("Due");
        _countOut = AddOutput<double>("Count");
    }

    public double Interval { get => _interval; set => Set(ref _interval, value); }

    public EveryUnit Unit { get => _unit; set => Set(ref _unit, value); }

    /// <summary>Whether the first pass of a run fires. On, a monitor checks as soon as it starts.</summary>
    public bool FirstPass { get => _firstPass; set => Set(ref _firstPass, value); }

    protected override void Reset()
    {
        _lastFired = null;
        _passesSinceFired = 0;
        _fired = 0;
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Remember(context);

        var interval = _intervalIn.ValueOr(Interval);
        if (double.IsNaN(interval) || interval < 0)
            interval = 0;

        var now = DateTime.UtcNow;
        bool due;
        if (_lastFired is not { } last)
        {
            due = FirstPass;
            _lastFired = now;
        }
        else if (Unit == EveryUnit.Passes)
        {
            due = _passesSinceFired + 1 >= Math.Max(1, Math.Round(interval));
        }
        else
        {
            due = now - last >= Length(interval, Unit);
        }

        if (due)
        {
            _lastFired = now;
            _passesSinceFired = 0;
            _fired++;
        }
        else
        {
            _passesSinceFired++;
        }

        _dueOut.Value = due;
        _countOut.Value = _fired;
        return Task.CompletedTask;
    }

    private static TimeSpan Length(double interval, EveryUnit unit) => unit switch
    {
        EveryUnit.Seconds => TimeSpan.FromSeconds(Math.Min(interval, 31_536_000)),
        EveryUnit.Minutes => TimeSpan.FromMinutes(Math.Min(interval, 525_600)),
        _ => TimeSpan.FromHours(Math.Min(interval, 8_760)),
    };
}

/// <summary>
/// True on the pass where a cron schedule comes round: once for each time the schedule fell between
/// the previous pass and this one, so <c>0 9 * * MON-FRI</c> fires on the first tick after nine on a
/// weekday. The first pass of a run only takes note of the time, so starting a run at 9:05 does not
/// fire for 9:00. The run's interval must be shorter than the schedule's gaps for none to be merged.
/// </summary>
/// <remarks>Next gives the coming occurrence, for showing when the branch will run next.</remarks>
[NodeDefinition("Flow/Cron Due", Description = "True on the pass where a cron schedule (e.g. 0 9 * * MON-FRI) comes round, for Run If in a continuous run.")]
public sealed class CronDueNode : StatefulNode
{
    private string _expression = "*/5 * * * *";
    private string _timeZone = string.Empty;

    private DateTime? _lastCheck;

    private readonly InputPort _expressionIn;
    private readonly OutputPort _dueOut;
    private readonly OutputPort _nextOut;

    public CronDueNode()
    {
        _expressionIn = AddInput<string>("Expression");
        _dueOut = AddOutput<bool>("Due");
        _nextOut = AddOutput<DateTime>("Next");
    }

    public string Expression { get => _expression; set => Set(ref _expression, value ?? string.Empty); }

    /// <summary>The zone the schedule is written in; empty means local.</summary>
    public string TimeZone { get => _timeZone; set => Set(ref _timeZone, value ?? string.Empty); }

    protected override void Reset() => _lastCheck = null;

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Remember(context);

        var cron = CronText.Parse(_expressionIn.ValueOr(Expression));
        var zone = TimeZones.Find(TimeZone) ?? TimeZoneInfo.Local;
        var now = DateTime.UtcNow;

        // An occurrence in (last check, now] is due; Cronos's "next after" excludes the start itself.
        _dueOut.Value = _lastCheck is { } last
                        && cron.GetNextOccurrence(last, zone) is { } occurrence
                        && occurrence <= now;

        _nextOut.Value = cron.GetNextOccurrence(now, zone)?.ToLocalTime();
        _lastCheck = now;
        return Task.CompletedTask;
    }
}
