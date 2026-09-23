namespace vNoder.Core;

/// <summary>
/// Decides whether an output may feed an input. Ports carry plain CLR types, so this is a couple of
/// assignability rules rather than a parallel type hierarchy.
/// </summary>
public static class PortTypeCompatibility
{
    /// <summary>The "any" port type.</summary>
    public static Type Any => typeof(object);

    /// <summary>
    /// True when <paramref name="outputType"/> may feed <paramref name="inputType"/>.
    /// </summary>
    /// <remarks>
    /// "Any" is permissive in <em>both</em> directions. An input declared as <c>object</c> accepts
    /// every output, and an output declared as <c>object</c> may feed any input: its real type is
    /// only known once the graph runs, so the check is deferred to evaluation, where
    /// <see cref="InputPort.ValueOr{T}"/> falls back rather than failing. Rejecting such a link at
    /// connection time would make a pass-through port (<c>OutputNode</c>) impossible to wire onward,
    /// which is the whole point of having one.
    /// </remarks>
    public static bool CanConnect(Type outputType, Type inputType) =>
        inputType == Any
        || outputType == Any
        || inputType == outputType
        || inputType.IsAssignableFrom(outputType);
}
