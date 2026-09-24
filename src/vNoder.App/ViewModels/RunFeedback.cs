using System.Diagnostics;
using System.Windows.Threading;
using vNoder.Core;

namespace vNoder.App.ViewModels;

/// <summary>
/// Shows a Run Once on the canvas: each node flashes as it finishes, in evaluation order, and a node
/// that is still evaluating is marked as running.
/// </summary>
/// <remarks>
/// <para>
/// A pass over ordinary nodes takes microseconds, so flashing nodes the instant they finish would
/// light the whole graph in one frame and show no order at all. Flashes are therefore queued and
/// released no closer together than <c>spacing</c>: 30 ms apart for a small graph, less for a large
/// one, so the whole cascade stays within <see cref="CascadeBudget"/>. A node that genuinely takes
/// time (an HTTP request, a bcrypt hash) holds the queue back naturally, since nothing after it can
/// finish first — so the playback is paced for readability but never shows an order that did not
/// happen.
/// </para>
/// <para>
/// Continuous runs pass no observer to the executor and get none of this: a graph re-running every
/// few hundred milliseconds would flicker constantly, and it already has its own loud indicator.
/// </para>
/// </remarks>
public sealed class RunFeedback
{
    private static readonly TimeSpan MaxSpacing = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan CascadeBudget = TimeSpan.FromMilliseconds(512);

    /// <summary>Roughly one frame, and the timer's resolution in practice (Windows ticks at ~15.6 ms).</summary>
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>How long a flash plays; matches the storyboard in the node template.</summary>
    private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(256);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<(NodeViewModel Node, bool Failed)> _pending = new();
    private readonly List<(NodeViewModel Node, TimeSpan Until)> _flashing = [];
    private readonly HashSet<NodeViewModel> _running = [];

    private Dictionary<NodeBase, NodeViewModel> _byModel = [];
    private TimeSpan _spacing = MaxSpacing;
    private TimeSpan _nextFlash;
    private int _generation;

    public RunFeedback(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => Advance();
    }

    /// <summary>
    /// Starts showing a new run over <paramref name="nodes"/>, abandoning whatever the previous one
    /// was still playing, and returns the observer to hand to the executor.
    /// </summary>
    public Action<NodeBase, NodeRunPhase> Begin(IEnumerable<NodeViewModel> nodes)
    {
        Reset();

        _byModel = nodes.ToDictionary(n => n.Model);
        _spacing = TimeSpan.FromTicks(Math.Min(MaxSpacing.Ticks, CascadeBudget.Ticks / Math.Max(1, _byModel.Count)));

        // Reports from a run that was superseded must not act on this one.
        var generation = ++_generation;
        return (node, phase) => _dispatcher.BeginInvoke(() => Apply(generation, node, phase), DispatcherPriority.Normal);
    }

    private void Apply(int generation, NodeBase model, NodeRunPhase phase)
    {
        if (generation != _generation || !_byModel.TryGetValue(model, out var node))
            return;

        if (phase == NodeRunPhase.Started)
        {
            node.IsRunning = true;
            _running.Add(node);
            return;
        }

        node.IsRunning = false;
        _running.Remove(node);

        // A skipped node did not run, so it does not flash; its dimmed style already says it was skipped.
        if (phase == NodeRunPhase.Skipped)
            return;

        // After a quiet spell (a slow node), the next flash is due now, not in the past: otherwise the
        // backlog would be released as one burst.
        var now = _clock.Elapsed;
        if (_pending.Count == 0 && _nextFlash < now)
            _nextFlash = now;

        _pending.Enqueue((node, phase == NodeRunPhase.Failed));
        Advance();
        _timer.Start();
    }

    private void Advance()
    {
        var now = _clock.Elapsed;

        while (_pending.Count > 0 && now >= _nextFlash)
        {
            var (node, failed) = _pending.Dequeue();
            node.Flash(failed);
            _flashing.Add((node, now + FlashDuration));

            // Shorter than a frame (a large graph), keep to the schedule, releasing several per tick
            // to catch up: that is what holds the cascade to its budget. Otherwise time the next flash
            // from this one. The spacing is then only a couple of timer ticks, so catching up after a
            // late tick would fire two flashes almost together and blur the very order being shown.
            _nextFlash = _spacing < Frame ? _nextFlash + _spacing : now + _spacing;
        }

        // Clear the flag once the flash has played, so that a node realized later (it was scrolled
        // off screen) does not replay a stale flash the moment its template loads.
        _flashing.RemoveAll(f =>
        {
            if (now < f.Until)
                return false;

            f.Node.EndFlash();
            return true;
        });

        if (_pending.Count == 0 && _flashing.Count == 0)
            _timer.Stop();
    }

    private void Reset()
    {
        _timer.Stop();
        _pending.Clear();

        foreach (var node in _running)
            node.IsRunning = false;
        _running.Clear();

        foreach (var (node, _) in _flashing)
            node.EndFlash();
        _flashing.Clear();

        _nextFlash = _clock.Elapsed;
    }
}
