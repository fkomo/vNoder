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

    public Task<GraphRunResult> RunOnceAsync(CancellationToken cancellationToken = default) =>
        RunOnceAsync(observer: null, cancellationToken);

    /// <summary>
    /// Runs one pass, reporting each node to <paramref name="observer"/> as it starts and as it
    /// finishes, in evaluation order. This is what lets the view show execution as it happens.
    /// </summary>
    /// <remarks>
    /// The observer is called on the evaluation thread, inline, so it must be quick and must marshal
    /// anything that touches UI state. Continuous runs take no observer, so they pay nothing for it.
    /// </remarks>
    public async Task<GraphRunResult> RunOnceAsync(
        Action<NodeBase, NodeRunPhase>? observer,
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EvaluateAsync(Interlocked.Increment(ref _tick), cancellationToken, observer).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// Holds evaluation off until the returned handle is disposed, waiting for a pass in progress to
    /// finish first. For edits that rewrite much of the graph at once, such as an undo, which must not
    /// happen underneath a continuous run's pass. A continuous tick that falls inside is skipped, as
    /// any tick is when the previous pass is still running.
    /// </summary>
    public async Task<IDisposable> ExclusiveAsync(CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateRelease(_runGate);
    }

    private sealed class GateRelease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
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

    /// <summary>
    /// Runs a node once per item of its iterating inputs (<see cref="Link.Iterate"/>) and gathers
    /// each output into a list, one entry per item. The node's own code is unaware of it: it sees one
    /// item on each iterating input, and its other inputs unchanged every time.
    /// </summary>
    /// <remarks>
    /// Several iterating inputs are paired by index, and must be the same length; a mismatch fails the
    /// node rather than guessing which items were meant to go together. An item that fails fails the
    /// whole node, as any failure does, with the item's position in the message. A value that is not
    /// a list counts as a list of one, and nothing as an empty list, which runs the node no times.
    /// </remarks>
    private static async Task EvaluatePerItemAsync(NodeBase node, NodeEvaluationContext context)
    {
        var iterating = node.Inputs.Where(input => input.Link is { Iterate: true }).ToList();
        var lists = iterating.Select(input => Items(input.Value)).ToList();

        var count = lists[0].Count;
        for (var i = 1; i < lists.Count; i++)
        {
            if (lists[i].Count != count)
            {
                throw new InvalidOperationException(
                    $"Lists differ in length: {iterating[0].Name} has {count} items, "
                    + $"{iterating[i].Name} has {lists[i].Count}.");
            }
        }

        var outputs = node.Outputs.Where(output => !ReferenceEquals(output, node.DonePort)).ToList();
        var gathered = outputs.Select(_ => new object?[count]).ToList();
        var whole = iterating.Select(input => input.Value).ToList();

        try
        {
            for (var item = 0; item < count; item++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                for (var i = 0; i < iterating.Count; i++)
                    iterating[i].Value = lists[i][item];

                foreach (var output in outputs)
                    output.Value = null;

                try
                {
                    await node.EvaluateAsync(context).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"Item {item + 1} of {count}: {exception.Message}", exception);
                }

                for (var o = 0; o < outputs.Count; o++)
                    gathered[o][item] = outputs[o].Value;
            }
        }
        finally
        {
            // The inputs show what arrived, the whole list, not whichever item was last.
            for (var i = 0; i < iterating.Count; i++)
                iterating[i].Value = whole[i];
        }

        for (var o = 0; o < outputs.Count; o++)
            outputs[o].Value = ToList(outputs[o].ValueType, gathered[o]);

        node.ItemCount = count;
    }

    private static IReadOnlyList<object?> Items(object? value) => value switch
    {
        null => [],
        Array list when PortTypeCompatibility.IsListValue(list) => [.. list.Cast<object?>()],
        _ => [value],
    };

    /// <summary>
    /// The gathered values as a typed list, so a node that reads a list input sees the type it asked
    /// for: text items become a <c>string[]</c>, which Join and the List nodes take. An "any" output
    /// is narrowed to its items' common type when they share one. A value-type list with a missing
    /// item cannot hold it, so it stays a list of "any" rather than inventing a zero.
    /// </summary>
    private static Array ToList(Type declared, object?[] values)
    {
        var element = declared;

        if (element == PortTypeCompatibility.Any)
        {
            var types = values.Where(v => v is not null).Select(v => v!.GetType()).Distinct().ToList();
            element = types.Count == 1 ? types[0] : PortTypeCompatibility.Any;
        }

        if (element == PortTypeCompatibility.Any || (element.IsValueType && values.Any(v => v is null)))
            return values;

        var list = Array.CreateInstance(element, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is { } value && element.IsInstanceOfType(value))
                list.SetValue(value, i);
            else if (values[i] is not null)
                return values;
        }

        return list;
    }

    private static void ClearOutputs(NodeBase node)
    {
        foreach (var output in node.Outputs)
            output.Value = null;
    }

    private TimeSpan IntervalOrDefault() =>
        TimeSpan.FromMilliseconds(Math.Clamp(Graph.IntervalMilliseconds, 16, 60_000));

    private async Task<GraphRunResult> EvaluateAsync(
        long tick,
        CancellationToken cancellationToken,
        Action<NodeBase, NodeRunPhase>? observer = null)
    {
        var context = new NodeEvaluationContext(tick, cancellationToken);
        var order = Graph.TopologicalOrder();
        var failed = new List<NodeBase>();
        var skipped = new List<NodeBase>();
        var evaluated = 0;

        // Nodes that failed or were skipped this pass. Whatever takes data from one of them is
        // skipped in turn, unless it merges branches.
        var blocked = new HashSet<NodeBase>();

        foreach (var node in order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Pull each input from the output it is wired to; unconnected inputs stay null so nodes
            // can fall back to their inline value. An input from a blocked node reads as empty too,
            // never as the value that node left behind on an earlier pass.
            var upstreamBlocked = false;
            foreach (var input in node.Inputs)
            {
                var source = input.Link?.Source;
                if (source is not null && blocked.Contains(source.Owner))
                {
                    input.Value = null;
                    upstreamBlocked = true;
                }
                else
                {
                    input.Value = source?.Value;
                }
            }

            var gatedOff = node.RunIfPort is { IsConnected: true } gate && gate.Value is not true;
            if (gatedOff || (upstreamBlocked && !node.Merges))
            {
                ClearOutputs(node);
                node.LastError = null;
                node.IsSkipped = true;
                node.ItemCount = null;
                blocked.Add(node);
                skipped.Add(node);
                observer?.Invoke(node, NodeRunPhase.Skipped);
                continue;
            }

            node.IsSkipped = false;
            node.ItemCount = null;
            observer?.Invoke(node, NodeRunPhase.Started);

            try
            {
                if (node.RunsPerItem)
                    await EvaluatePerItemAsync(node, context).ConfigureAwait(false);
                else
                    await node.EvaluateAsync(context).ConfigureAwait(false);

                if (node.DonePort is { } done)
                    done.Value = true;

                node.LastError = null;
                evaluated++;
                observer?.Invoke(node, NodeRunPhase.Succeeded);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One bad node must not abort the pass, but nothing downstream runs on what it left
                // behind: its outputs are cleared, and the nodes that take data from it are skipped.
                ClearOutputs(node);
                node.LastError = exception.Message;
                failed.Add(node);
                blocked.Add(node);
                observer?.Invoke(node, NodeRunPhase.Failed);
            }
        }

        var result = new GraphRunResult(tick, evaluated, failed, skipped);
        Evaluated?.Invoke(this, result);
        return result;
    }
}

/// <summary>What <see cref="GraphExecutor.RunOnceAsync(Action{NodeBase, NodeRunPhase}?, CancellationToken)"/> reports about a node.</summary>
public enum NodeRunPhase
{
    /// <summary>The node's inputs are pulled and it is about to evaluate.</summary>
    Started,

    /// <summary>The node evaluated without error.</summary>
    Succeeded,

    /// <summary>The node threw; its message is on <see cref="NodeBase.LastError"/>.</summary>
    Failed,

    /// <summary>
    /// The node did not run: its Run If was wired and not true, or a node it takes data from failed or
    /// was skipped. Reported without a preceding <see cref="Started"/>.
    /// </summary>
    Skipped,
}
