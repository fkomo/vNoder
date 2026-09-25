namespace vNoder.Core;

/// <summary>
/// A free-text note attached to one node or group. Purely presentational, like <see cref="NodeGroup"/>:
/// evaluation never looks at notes. It lives here only so that it travels with the saved document.
/// </summary>
/// <remarks>
/// The note's position is an offset from its target's top-left corner rather than a place on the
/// canvas, so moving the node (or the group) carries its note along with nothing to keep in sync.
/// A note whose target is removed goes with it (<see cref="Graph"/> prunes it).
/// </remarks>
public sealed class GraphNote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The id of the node or group the note is attached to.</summary>
    public Guid TargetId { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Offset of the note's top-left corner from its target's top-left corner.</summary>
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    /// <summary>Set once the note has been resized; zero means the view's default width.</summary>
    public double Width { get; set; }

    /// <summary>Set once the note has been resized; zero means as tall as its text.</summary>
    public double Height { get; set; }

    public override string ToString() => Text.Length > 30 ? Text[..30] + "…" : Text;
}
