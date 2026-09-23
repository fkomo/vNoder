namespace vNoder.App.ViewModels;

/// <summary>
/// One row of a node body: the input at this index on the left, the output at this index on the right.
/// Either side may be absent.
/// </summary>
public sealed class NodeRowViewModel(PortViewModel? input, PortViewModel? output)
{
    public PortViewModel? Input { get; } = input;
    public PortViewModel? Output { get; } = output;
}
