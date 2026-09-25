using vNoder.Core;

namespace vNoder.Nodes;

// Nodes that remember earlier passes, which is what a continuous run needs to react to change rather
// than act on every tick. Their memory is private and never saved, and starts over whenever a new
// continuous run starts (NodeEvaluationContext.Run); between Run Once presses it carries on, so a
// graph can be stepped through by hand.

/// <summary>A node with memory: <see cref="Reset"/> is called before the first pass of each run.</summary>
public abstract class StatefulNode : NodeBase
{
    private long? _run;

    /// <summary>Call first in <see cref="NodeBase.EvaluateAsync"/>; resets the memory when a new run has started.</summary>
    protected void Remember(NodeEvaluationContext context)
    {
        if (_run == context.Run)
            return;

        _run = context.Run;
        Reset();
    }

    protected abstract void Reset();
}

[NodeDefinition("Flow/Changed", Description = "True when the value differs from the previous pass — react to change instead of every tick.")]
public sealed class ChangedNode : StatefulNode
{
    private bool _firstCounts;
    private bool _seen;
    private object? _last;

    private readonly InputPort _in;
    private readonly OutputPort _changedOut;
    private readonly OutputPort _valueOut;
    private readonly OutputPort _previousOut;

    public ChangedNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _changedOut = AddOutput<bool>("Changed");
        _valueOut = AddOutput("Value", PortTypeCompatibility.Any);
        _previousOut = AddOutput("Previous", PortTypeCompatibility.Any);
    }

    /// <summary>
    /// Whether the first value a run sees counts as a change. Off, a monitor does not fire just for
    /// having started; on, it acts once on whatever is there.
    /// </summary>
    public bool FirstCounts
    {
        get => _firstCounts;
        set => Set(ref _firstCounts, value);
    }

    protected override void Reset()
    {
        _seen = false;
        _last = null;
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Remember(context);
        var value = _in.Value;

        _changedOut.Value = _seen ? !EqualsNode.AreEqual(_last, value, ignoreCase: false) : FirstCounts;
        _previousOut.Value = _last;
        _valueOut.Value = value;

        _seen = true;
        _last = value;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Flow/Counter", Description = "Counts passes, or the passes where Count If is true; Reset starts over.")]
public sealed class CounterNode : StatefulNode
{
    private double _start;
    private double _step = 1;
    private double? _count;

    private readonly InputPort _countIfIn;
    private readonly InputPort _resetIn;
    private readonly OutputPort _out;

    public CounterNode()
    {
        _countIfIn = AddInput<bool>("Count If");
        _resetIn = AddInput<bool>("Reset");
        _out = AddOutput<double>("Count");
    }

    /// <summary>The value after a reset, before anything is counted.</summary>
    public double Start
    {
        get => _start;
        set => Set(ref _start, value);
    }

    public double Step
    {
        get => _step;
        set => Set(ref _step, value);
    }

    protected override void Reset() => _count = null;

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Remember(context);
        var count = _count ?? Start;

        if (_resetIn.Value is true)
            count = Start;
        else if (_countIfIn.ValueOr(true))
            count += Step;

        _count = count;
        _out.Value = count;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Flow/Previous Value", Description = "The value this input had on the previous pass.")]
public sealed class PreviousValueNode : StatefulNode
{
    private object? _last;

    private readonly InputPort _in;
    private readonly OutputPort _previousOut;
    private readonly OutputPort _valueOut;

    public PreviousValueNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _previousOut = AddOutput("Previous", PortTypeCompatibility.Any);
        _valueOut = AddOutput("Value", PortTypeCompatibility.Any);
    }

    protected override void Reset() => _last = null;

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Remember(context);
        _previousOut.Value = _last;
        _valueOut.Value = _in.Value;
        _last = _in.Value;
        return Task.CompletedTask;
    }
}

[NodeDefinition("Flow/History", Description = "Collects the last N values across passes into a list, oldest first.")]
public sealed class HistoryNode : StatefulNode
{
    private double _length = 10;
    private readonly List<object?> _values = [];

    private readonly InputPort _in;
    private readonly InputPort _lengthIn;
    private readonly OutputPort _out;
    private readonly OutputPort _countOut;

    public HistoryNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _lengthIn = AddInput<double>("Length");
        _out = AddOutput("Values", PortTypeCompatibility.AnyList);
        _countOut = AddOutput<double>("Count");
    }

    /// <summary>How many values to keep, 1 to 100,000.</summary>
    public double Length
    {
        get => _length;
        set => Set(ref _length, value);
    }

    protected override void Reset() => _values.Clear();

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        Remember(context);

        // An empty pass (nothing arrived) is not a value worth remembering.
        if (_in.Value is not null)
            _values.Add(_in.Value);

        var length = _lengthIn.ValueOr(Length);
        var keep = double.IsNaN(length) ? 10 : (int)Math.Clamp(length, 1, 100_000);
        if (_values.Count > keep)
            _values.RemoveRange(0, _values.Count - keep);

        _out.Value = NodeValues.ToTypedList(_values);
        _countOut.Value = (double)_values.Count;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Waits, then passes its value on. With Run If / Done it spaces out side effects ("write, wait, then
/// read"); in a continuous run the pass waits with it, and ticks that fall inside are skipped.
/// </summary>
[NodeDefinition("Flow/Delay", Description = "Waits a number of milliseconds, then passes the value on.")]
public sealed class DelayNode : NodeBase
{
    private double _milliseconds = 1000;

    private readonly InputPort _in;
    private readonly InputPort _millisecondsIn;
    private readonly OutputPort _out;

    public DelayNode()
    {
        _in = AddInput("Value", PortTypeCompatibility.Any);
        _millisecondsIn = AddInput<double>("Milliseconds");
        _out = AddOutput("Value", PortTypeCompatibility.Any);

        AddRunGate();
    }

    public double Milliseconds
    {
        get => _milliseconds;
        set => Set(ref _milliseconds, value);
    }

    public override async Task EvaluateAsync(NodeEvaluationContext context)
    {
        var milliseconds = _millisecondsIn.ValueOr(Milliseconds);

        // Clamped to an hour: a stray large value should not hang a run for days.
        var wait = double.IsNaN(milliseconds) ? 0 : Math.Clamp(milliseconds, 0, 3_600_000);
        await Task.Delay(TimeSpan.FromMilliseconds(wait), context.CancellationToken).ConfigureAwait(false);
        _out.Value = _in.Value;
    }
}
