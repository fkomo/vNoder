namespace vNoder.Core;

/// <summary>Per-run state handed to every node's <see cref="NodeBase.EvaluateAsync"/>.</summary>
public sealed class NodeEvaluationContext(
    long tick,
    CancellationToken cancellationToken,
    long run = 0,
    INodeHost? host = null,
    ConsoleCapture? console = null)
{
    /// <summary>0 for a one-shot run; increments once per clock tick in continuous mode.</summary>
    public long Tick { get; } = tick;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// Which run this pass belongs to: it changes each time a continuous run starts, and one-shot
    /// passes carry the latest. Nodes that remember earlier passes (Counter, Changed, History) start
    /// over when it changes, so a new continuous run begins fresh while Run Once steps through them. A
    /// number rather than a first-pass flag, so a node skipped on the first pass still notices.
    /// </summary>
    public long Run { get; } = run;

    /// <summary>
    /// What the application offers beyond the graph (clipboard, notifications), or null when nothing
    /// hosts the graph, as in a test harness. Nodes that need it fail with a message when it is absent.
    /// </summary>
    public INodeHost? Host { get; } = host;

    /// <summary>
    /// Where CLI Write records what it writes, for the Standard Output and Standard Error nodes; null
    /// in a harness that runs nodes without an executor.
    /// </summary>
    public ConsoleCapture? Console { get; } = console;
}

/// <summary>
/// Services that only the application can provide, because they need its UI thread or its window.
/// Core and Nodes carry no UI reference, so a node reaches them through this.
/// </summary>
public interface INodeHost
{
    Task<string> GetClipboardTextAsync(CancellationToken cancellationToken);

    Task SetClipboardTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Shows a desktop notification, optionally with a sound.</summary>
    Task NotifyAsync(string title, string message, bool sound, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the code the process exits with once the run is over. Only the command-line runner has
    /// one to give; the editor ignores it. The last call in a pass wins.
    /// </summary>
    void SetExitCode(int code);

    /// <summary>
    /// The value given for a named command-line parameter (<c>--name value</c>), or null when it was
    /// not given. Only the command-line runner has any; in the editor every parameter is absent.
    /// </summary>
    string? GetParameter(string name);

    /// <summary>
    /// Everything piped into the process's standard input, or null when nothing was. Only the
    /// command-line runner reads it; the editor has none, even when started with input redirected.
    /// </summary>
    Task<string?> ReadStandardInputAsync(CancellationToken cancellationToken);
}
