using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>
/// Free text on the canvas, attached to nothing: a heading for an area, an explanation, a to-do. It
/// has no ports and does nothing when run; the view draws it as a note rather than as a node.
/// </summary>
/// <remarks>
/// A node rather than a new kind of canvas item, so selecting, moving, grouping, copy and paste,
/// undo and saving all come for free. A note about one particular node or group is a linked note
/// instead (right-click it, Add note), which follows what it describes.
/// </remarks>
[NodeDefinition("Output/Note", Description = "Free text on the canvas, not linked to any node.", Resizable = true)]
public sealed class NoteNode : NodeBase
{
    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context) => Task.CompletedTask;
}
