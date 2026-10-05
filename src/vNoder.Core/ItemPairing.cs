namespace vNoder.Core;

/// <summary>
/// How a node running per item combines the lists on its per-item inputs (<see cref="NodeBase.Pairing"/>).
/// Saved by name, so members may be added in any order, but a name never changes.
/// </summary>
public enum ItemPairing
{
    /// <summary>Item i of each list together; lists of different lengths fail the node.</summary>
    ByIndex,

    /// <summary>As many runs as the longest list; a shorter list repeats its last item.</summary>
    Longest,

    /// <summary>Every combination: the first list varies slowest, so A1 B1, A1 B2, A2 B1, A2 B2.</summary>
    CrossProduct,
}
