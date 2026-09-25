namespace vNoder.Core;

/// <summary>Per-run state handed to every node's <see cref="NodeBase.EvaluateAsync"/>.</summary>
public sealed class NodeEvaluationContext(
    long tick,
    CancellationToken cancellationToken,
    long run = 0,
    INodeHost? host = null)
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
}
