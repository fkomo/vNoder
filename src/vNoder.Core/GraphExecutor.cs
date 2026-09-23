namespace vNoder.Core;

/// <summary>
/// Runs a <see cref="Graph"/> once, or continuously off a clock node.
/// </summary>
/// <remarks>
/// Continuous mode is driven by <see cref="Graph.IntervalMilliseconds"/>, a graph-level setting, so
/// any graph can be run continuously without wiring anything up.
/// </remarks>
public sealed class GraphExecutor(Graph graph)
{
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private CancellationTokenSource? _continuousCancellation;
    private Task? _continuousLoop;
    private long _tick;

    public Graph Graph { get; } = graph;

    public bool IsRunningContinuously => _continuousLoop is { IsCompleted: false };

    /// <summary>
    /// Raised after each completed pass, on the evaluation thread. Subscribers that touch UI state are
    /// responsible for marshalling — see <see cref="NodeBase.RaiseBindingsChanged"/>.
    /// </summary>
    public event EventHandler<GraphRunResult>? Evaluated;

    public async Task<GraphRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EvaluateAsync(Interlocked.Increment(ref _tick), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public void StartContinuous()
    {
        if (IsRunningContinuously)
            return;

        _continuousCancellation?.Dispose();
        _continuousCancellation = new CancellationTokenSource();
        var token = _continuousCancellation.Token;

        _continuousLoop = Task.Run(() => ContinuousLoopAsync(token), CancellationToken.None);
    }

    public async Task StopContinuousAsync()
    {
        if (_continuousCancellation is null)
            return;

        await _continuousCancellation.CancelAsync().ConfigureAwait(false);

        if (_continuousLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on stop.
            }
        }

        _continuousLoop = null;
        _continuousCancellation.Dispose();
        _continuousCancellation = null;
    }

    private async Task ContinuousLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(IntervalOrDefault());

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Pick up interval edits made while running.
            var interval = IntervalOrDefault();
            if (timer.Period != interval)
                timer.Period = interval;

            // Skip rather than queue when the previous pass is still going, so a slow tick cannot
            // pile up behind itself.
            if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                continue;

            try
            {
                await EvaluateAsync(Interlocked.Increment(ref _tick), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _runGate.Release();
            }
        }
    }

    private TimeSpan IntervalOrDefault() =>
        TimeSpan.FromMilliseconds(Math.Clamp(Graph.IntervalMilliseconds, 16, 60_000));

    private async Task<GraphRunResult> EvaluateAsync(long tick, CancellationToken cancellationToken)
    {
        var context = new NodeEvaluationContext(tick, cancellationToken);
        var order = Graph.TopologicalOrder();
        var failed = new List<NodeBase>();
        var evaluated = 0;

        foreach (var node in order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Pull each input from the output it is wired to; unconnected inputs stay null so nodes
            // can fall back to their inline value.
            foreach (var input in node.Inputs)
                input.Value = input.Link?.Source.Value;

            try
            {
                await node.EvaluateAsync(context).ConfigureAwait(false);
                node.LastError = null;
                evaluated++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One bad node must not abort the pass; downstream nodes see its stale/null outputs.
                node.LastError = exception.Message;
                failed.Add(node);
            }
        }

        var result = new GraphRunResult(tick, evaluated, failed);
        Evaluated?.Invoke(this, result);
        return result;
    }
}
