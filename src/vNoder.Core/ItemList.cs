namespace vNoder.Core;

/// <summary>
/// The "list" port type (<see cref="PortTypeCompatibility.ItemList"/>), for inputs that take a list of
/// anything: text, numbers, booleans, dates. Never a value: what arrives is the list itself, an array,
/// and the node reads its items with <c>NodeValues</c>. A marker type because ports carry CLR types, and
/// no CLR type says "any one-level array, but not an array of arrays".
/// </summary>
public sealed class ItemList
{
    private ItemList()
    {
    }
}
