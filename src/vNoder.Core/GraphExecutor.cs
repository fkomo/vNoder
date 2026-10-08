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
    private long _run;

    public Graph Graph { get; } = graph;

    public bool IsRunningContinuously => _continuousLoop is { IsCompleted: false };

    /// <summary>Clipboard and notifications, handed to nodes on <see cref="NodeEvaluationContext.Host"/>.</summary>
    public INodeHost? Host { get; set; }

    /// <summary>
    /// What CLI Write wrote in the current run, for the Standard Output and Standard Error nodes. Cleared
    /// by each Run Once and when a continuous run starts; a continuous run's ticks accumulate.
    /// </summary>
    public ConsoleCapture Console { get; } = new();

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
            Console.Clear();
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
        Interlocked.Increment(ref _run);
        Console.Clear();
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
    /// each output into a list, one entry per run. The node's own code is unaware of it: it sees one
    /// item on each iterating input, and its other inputs unchanged every time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Several iterating inputs are combined as <see cref="NodeBase.Pairing"/> says. By index (the
    /// default) they must be the same length; a mismatch fails the node rather than guessing which
    /// items were meant to go together. Longest runs as many times as the longest list, a shorter one
    /// repeating its last item. Cross product runs every combination, the first input varying slowest.
    /// A value that is not a list counts as a list of one, and nothing as an empty list, which runs the
    /// node no times (in any pairing, since there is nothing to pair it with).
    /// </para>
    /// <para>
    /// An item that fails fails the whole node, with the item's position in the message, unless
    /// <see cref="NodeBase.SkipFailedItems"/> is set: then its results are left empty, so positions
    /// still line up with the input, and the node succeeds with <see cref="NodeBase.ItemWarning"/>.
    /// </para>
    /// </remarks>
    private static async Task EvaluatePerItemAsync(NodeBase node, NodeEvaluationContext context)
    {
        var iterating = node.Inputs.Where(input => input.Link is { Iterate: true }).ToList();
        var lists = iterating.Select(input => Items(input.Value)).ToList();
        var (count, indexOf) = Pair(node.Pairing, iterating, lists);

        var outputs = node.Outputs.Where(output => !ReferenceEquals(output, node.DonePort)).ToList();
        var gathered = outputs.Select(_ => new object?[count]).ToList();
        var whole = iterating.Select(input => input.Value).ToList();
        var failures = 0;
        string? firstFailure = null;

        try
        {
            for (var item = 0; item < count; item++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                for (var i = 0; i < iterating.Count; i++)
                    iterating[i].Value = lists[i][indexOf(item, i)];

                foreach (var output in outputs)
                    output.Value = null;

                try
                {
                    await node.EvaluateAsync(context).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var message = $"Item {item + 1} of {count}: {exception.Message}";
                    if (!node.SkipFailedItems)
                        throw new InvalidOperationException(message, exception);

                    // Skipped: its slot stays empty, so later items keep their positions.
                    failures++;
                    firstFailure ??= message;
                    continue;
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

        node.ItemsGathered();
        node.ItemCount = count;
        node.ItemWarning = failures == 0
            ? null
            : $"{failures} of {count} items failed and were skipped. {firstFailure}";
    }

    /// <summary>The most runs one pass may make, so a cross product of long lists fails rather than hangs.</summary>
    private const int MaxItemRuns = 1_000_000;

    /// <summary>
    /// How many times a per-item node runs, and which item of input <c>i</c> run <c>n</c> takes.
    /// </summary>
    private static (int Count, Func<int, int, int> IndexOf) Pair(
        ItemPairing pairing,
        IReadOnlyList<InputPort> inputs,
        IReadOnlyList<IReadOnlyList<object?>> lists)
    {
        var lengths = lists.Select(list => list.Count).ToArray();
        if (lengths.Length == 1 || lengths.Any(length => length == 0))
            return (lengths.Length == 1 ? lengths[0] : 0, (item, _) => item);

        switch (pairing)
        {
            case ItemPairing.Longest:
                return (lengths.Max(), (item, i) => Math.Min(item, lengths[i] - 1));

            case ItemPairing.CrossProduct:
                var total = lengths.Aggregate(1L, (product, length) => product * length);
                if (total > MaxItemRuns)
                {
                    throw new InvalidOperationException(
                        $"Every combination of these lists is {total:N0} runs, more than the {MaxItemRuns:N0} allowed.");
                }

                // Like an odometer: the last input turns fastest, the first slowest.
                var strides = new int[lengths.Length];
                var stride = 1;
                for (var i = lengths.Length - 1; i >= 0; i--)
                {
                    strides[i] = stride;
                    stride *= lengths[i];
                }

                return ((int)total, (item, i) => item / strides[i] % lengths[i]);

            default:
                for (var i = 1; i < lengths.Length; i++)
                {
                    if (lengths[i] != lengths[0])
                    {
                        throw new InvalidOperationException(
                            $"Lists differ in length: {inputs[0].Name} has {lengths[0]} items, {inputs[i].Name} has {lengths[i]}. "
                            + "Pair them by longest list or every combination from the node's right-click menu.");
                    }
                }

                return (lengths[0], (item, _) => item);
        }
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
        var context = new NodeEvaluationContext(tick, cancellationToken, Interlocked.Read(ref _run), Host, Console);
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
                node.ItemWarning = null;
                blocked.Add(node);
                skipped.Add(node);
                observer?.Invoke(node, NodeRunPhase.Skipped);
                continue;
            }

            node.IsSkipped = false;
            node.ItemCount = null;
            node.ItemWarning = null;
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
                node.ItemWarning = null;
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
