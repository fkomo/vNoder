namespace vNoder.Core;

/// <summary>
/// Decides whether an output may feed an input. Ports carry plain CLR types, so this is a couple of
/// assignability rules rather than a parallel type hierarchy.
/// </summary>
public static class PortTypeCompatibility
{
    /// <summary>The "any" port type.</summary>
    public static Type Any => typeof(object);

    /// <summary>A list of "any": what a per-item node with an "any" output produces.</summary>
    public static Type AnyList => typeof(object[]);

    /// <summary>
    /// The "list" port type: an input that takes any list whole, of text, numbers, booleans or dates
    /// (the List nodes, Join). A list of lists does not fit whole but fits per item, so it runs the node
    /// once per inner list, which is how Chunk into Join joins each chunk.
    /// </summary>
    public static Type ItemList => typeof(ItemList);

    /// <summary>A list whose items are not lists themselves.</summary>
    private static bool IsFlatList(Type type) => type.IsArray && type != typeof(byte[]) && !IsList(ElementOf(type));

    /// <summary>
    /// True when <paramref name="outputType"/> may feed <paramref name="inputType"/> as a whole value.
    /// </summary>
    /// <remarks>
    /// "Any" is permissive in <em>both</em> directions. An input declared as <c>object</c> accepts
    /// every output, and an output declared as <c>object</c> may feed any input: its real type is
    /// only known once the graph runs, so the check is deferred to evaluation, where
    /// <see cref="InputPort.ValueOr{T}"/> falls back rather than failing. Rejecting such a link at
    /// connection time would make a pass-through port (<c>OutputNode</c>) impossible to wire onward,
    /// which is the whole point of having one. A list of "any" is permissive the same way towards
    /// list inputs: the items' real type is only known at run time.
    /// </remarks>
    public static bool CanConnect(Type outputType, Type inputType) =>
        inputType == ItemList
            ? outputType == Any || outputType == AnyList || IsFlatList(outputType)
            : inputType == Any
              || outputType == Any
              || inputType == outputType
              || (outputType == AnyList && IsList(inputType))
              || inputType.IsAssignableFrom(outputType);

    /// <summary>
    /// True when each item of <paramref name="outputType"/> may feed <paramref name="inputType"/>, so a
    /// link between them can run its target once per item. An "any" output may be a list at run
    /// time, so it may be iterated too.
    /// </summary>
    public static bool CanIterate(Type outputType, Type inputType) =>
        outputType == Any
        || (IsList(outputType) && CanConnect(ElementOf(outputType), inputType));

    /// <summary>
    /// A list type. Lists are arrays, except <c>byte[]</c>, which is one value (a buffer or an image)
    /// rather than a list of numbers.
    /// </summary>
    public static bool IsList(Type type) => type == ItemList || (type.IsArray && type != typeof(byte[]));

    /// <summary>A list value, by the same rule as <see cref="IsList(Type)"/>.</summary>
    public static bool IsListValue(object? value) => value is Array and not byte[];

    /// <summary>The list of <paramref name="element"/>: <c>double</c> gives <c>double[]</c>.</summary>
    public static Type ListOf(Type element) => element.MakeArrayType();

    /// <summary>The item type of a list type.</summary>
    public static Type ElementOf(Type list) => list.GetElementType() ?? Any;

    /// <summary>A short human name for a port type, as used in messages and tooltips.</summary>
    public static string Describe(Type type) => type switch
    {
        _ when type == typeof(double) => "number",
        _ when type == typeof(string) => "text",
        _ when type == typeof(bool) => "boolean",
        _ when type == typeof(byte[]) => "bytes",
        _ when type == typeof(DateTime) => "date/time",
        _ when type == typeof(object) => "any",
        _ when type == ItemList => "list",
        _ when IsList(type) => $"{Describe(ElementOf(type))} list",
        _ => type.Name,
    };
}
