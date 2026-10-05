using System.Globalization;
using System.Text;

namespace vNoder.Nodes;

/// <summary>Conversions shared by nodes whose "any" input accepts more than one kind of value.</summary>
internal static class NodeValues
{
    /// <summary>
    /// Bytes as-is, text as UTF-8, nothing as empty. Anything else is refused with a message naming
    /// what the node was trying to do.
    /// </summary>
    public static byte[] AsBytes(object? value, string purpose) => value switch
    {
        null => [],
        byte[] raw => raw,
        string text => Encoding.UTF8.GetBytes(text),
        var other => throw new InvalidOperationException($"{purpose} needs text or bytes, got {other.GetType().Name}."),
    };

    /// <summary>
    /// A value as the text it displays as: the value and nothing else, with no glyphs, byte counts or
    /// placeholder, since Output shows exactly this and it is meant to be copied out verbatim. Bytes
    /// are lower-case hex, a list is one item per line, and a date is ISO 8601. A list inside a list
    /// (what a per-item run of Split produces) is one line per inner list, as <c>[a, b, c]</c>.
    /// </summary>
    public static string ToText(object? value) => value switch
    {
        string[] parts => string.Join(Environment.NewLine, parts),
        Array list and not byte[] => string.Join(Environment.NewLine, list.Cast<object?>().Select(ItemText)),
        _ => ItemText(value),
    };

    private static string ItemText(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("0.######", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes).ToLowerInvariant(),
        Array list => $"[{string.Join(", ", list.Cast<object?>().Select(ItemText))}]",
        DateTime moment => moment.ToString("o", CultureInfo.InvariantCulture),
        string text => text,
        var other => other.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Nothing, empty text, an empty buffer or an empty list. With <paramref name="whitespaceIsEmpty"/>,
    /// text of only spaces and line breaks counts as empty too.
    /// </summary>
    public static bool IsEmpty(object? value, bool whitespaceIsEmpty = false) => value switch
    {
        null => true,
        string text => whitespaceIsEmpty ? string.IsNullOrWhiteSpace(text) : text.Length == 0,
        byte[] bytes => bytes.Length == 0,
        Array list => list.Length == 0,
        _ => false,
    };

    /// <summary>
    /// A value as a number: a number itself, text that parses (invariant), a boolean as 1 or 0.
    /// Anything else, including text that does not parse, is null.
    /// </summary>
    public static double? AsNumber(object? value) => value switch
    {
        double number => number,
        bool flag => flag ? 1 : 0,
        string text when double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// Entries typed as one text (separated by commas, semicolons or lines) or wired as a list, trimmed,
    /// empty ones dropped: host names, email recipients.
    /// </summary>
    public static string[] SplitList(object? value) => value switch
    {
        null => [],
        string[] list => list.SelectMany(SplitText).ToArray(),
        _ => SplitText(ToText(value)),
    };

    private static string[] SplitText(string text) =>
        text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A list input (<see cref="PortTypeCompatibility.ItemList"/>) as text items: a text list as it is,
    /// a list of numbers, booleans or dates through <see cref="ToText"/>, so they sort, filter and join
    /// the way they display. A single value is a list of one, nothing an empty list.
    /// </summary>
    public static string[] TextItems(object? value) => value switch
    {
        string[] text => text,
        _ => [.. Items(value).Select(ToText)],
    };

    /// <summary>The items of a list value; a single value is a list of one, nothing an empty list.</summary>
    public static IReadOnlyList<object?> Items(object? value) => value switch
    {
        null => [],
        Array list and not byte[] => [.. list.Cast<object?>()],
        _ => [value],
    };

    /// <summary>
    /// Values as the most specific list they fit: <c>double[]</c>, <c>string[]</c>, <c>bool[]</c> and
    /// so on when every item is that type, otherwise a list of "any". Text lists are what Join and the
    /// List nodes take, so a list assembled from text items should reach them as one.
    /// </summary>
    public static Array ToTypedList(IReadOnlyList<object?> values)
    {
        var types = values.Select(v => v?.GetType()).Distinct().ToList();
        if (types.Count != 1 || types[0] is not { } element)
            return values.ToArray();

        var list = Array.CreateInstance(element, values.Count);
        for (var i = 0; i < values.Count; i++)
            list.SetValue(values[i], i);

        return list;
    }

    /// <summary>A culture by name, or invariant for an empty name. Predefined cultures only.</summary>
    public static CultureInfo Culture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CultureInfo.InvariantCulture;

        try
        {
            // Predefined only: under ICU any well-formed name is otherwise accepted as a made-up
            // culture with invariant formatting, which would hide a typo.
            return CultureInfo.GetCultureInfo(name.Trim(), predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            throw new InvalidOperationException($"Unknown culture '{name}'.");
        }
    }
}
