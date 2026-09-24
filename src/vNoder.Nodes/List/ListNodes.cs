using System.Globalization;
using vNoder.Core;

namespace vNoder.Nodes;

// Operations on the text list type (string[]), as produced by Split, Lines, File Search, Regex Extract
// All and JSON arrays. Like the text nodes they clamp rather than throw: an index past the end reads as
// "not found", and a slice past the end is just shorter.

/// <summary>A node reading one list; the port declaration shared by every list node.</summary>
public abstract class ListNode : NodeBase
{
    protected ListNode() => ItemsIn = AddInput<string[]>("Items");

    protected InputPort ItemsIn { get; }

    protected string[] Items => ItemsIn.ValueOr(Array.Empty<string>());
}

/// <summary>A list in, a list out.</summary>
public abstract class ListTransformNode : ListNode
{
    private readonly OutputPort _out;
    private readonly OutputPort _countOut;

    protected ListTransformNode()
    {
        _out = AddOutput<string[]>("Items");
        _countOut = AddOutput<double>("Count");
    }

    protected abstract string[] Apply(string[] items);

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var result = Apply(Items);
        _out.Value = result;
        _countOut.Value = (double)result.Length;
        return Task.CompletedTask;
    }
}

[NodeDefinition("List/Count", Description = "The number of items in a list.")]
public sealed class ListCountNode : ListNode
{
    private readonly OutputPort _out;

    public ListCountNode() => _out = AddOutput<double>("Count");

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _out.Value = (double)Items.Length;
        return Task.CompletedTask;
    }
}

[NodeDefinition("List/Item At", Description = "The item at an index, counting from 0; a negative index counts from the end.")]
public sealed class ListItemAtNode : ListNode
{
    private double _index;

    private readonly InputPort _indexIn;
    private readonly OutputPort _itemOut;
    private readonly OutputPort _foundOut;

    public ListItemAtNode()
    {
        _indexIn = AddInput<double>("Index");
        _itemOut = AddOutput<string>("Item");
        _foundOut = AddOutput<bool>("Found");
    }

    public double Index
    {
        get => _index;
        set => Set(ref _index, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        (_itemOut.Value, _foundOut.Value) = ListIndex.At(Items, _indexIn.ValueOr(Index));
        return Task.CompletedTask;
    }
}

[NodeDefinition("List/First", Description = "The first item of a list.")]
public sealed class ListFirstNode : ListNode
{
    private readonly OutputPort _itemOut;
    private readonly OutputPort _foundOut;

    public ListFirstNode()
    {
        _itemOut = AddOutput<string>("Item");
        _foundOut = AddOutput<bool>("Found");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        (_itemOut.Value, _foundOut.Value) = ListIndex.At(Items, 0);
        return Task.CompletedTask;
    }
}

[NodeDefinition("List/Last", Description = "The last item of a list.")]
public sealed class ListLastNode : ListNode
{
    private readonly OutputPort _itemOut;
    private readonly OutputPort _foundOut;

    public ListLastNode()
    {
        _itemOut = AddOutput<string>("Item");
        _foundOut = AddOutput<bool>("Found");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        (_itemOut.Value, _foundOut.Value) = ListIndex.At(Items, -1);
        return Task.CompletedTask;
    }
}

public enum ListSortMode
{
    /// <summary>Ordinal text order: "10" before "2".</summary>
    Text,

    /// <summary>Numbers inside the text compare as numbers: "file2" before "file10".</summary>
    Natural,

    /// <summary>Items that parse as numbers compare numerically and come first; the rest follow as text.</summary>
    Numeric,
}

[NodeDefinition("List/Sort", Description = "Sorts a list as text, naturally (file2 before file10) or numerically.")]
public sealed class ListSortNode : ListTransformNode
{
    private ListSortMode _mode = ListSortMode.Natural;
    private bool _descending;
    private bool _ignoreCase = true;

    public ListSortMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    public bool Descending
    {
        get => _descending;
        set => Set(ref _descending, value);
    }

    public bool IgnoreCase
    {
        get => _ignoreCase;
        set => Set(ref _ignoreCase, value);
    }

    protected override string[] Apply(string[] items)
    {
        IComparer<string> comparer = Mode switch
        {
            ListSortMode.Natural => new NaturalComparer(IgnoreCase),
            ListSortMode.Numeric => new NumericComparer(IgnoreCase),
            _ => IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal,
        };

        // OrderBy is stable, so equal items keep their order either way.
        return (Descending ? items.OrderByDescending(i => i, comparer) : items.OrderBy(i => i, comparer)).ToArray();
    }

    private sealed class NumericComparer(bool ignoreCase) : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var xNumber = double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var a);
            var yNumber = double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var b);

            return (xNumber, yNumber) switch
            {
                (true, true) => a.CompareTo(b),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(x, y, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            };
        }
    }

    private sealed class NaturalComparer(bool ignoreCase) : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0, j = 0;

            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var xStart = i;
                    var yStart = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                    // Compare the digit runs by value without parsing, so any length works: strip
                    // leading zeros, then a longer run is bigger, then compare digit by digit.
                    var xDigits = x.AsSpan(xStart, i - xStart).TrimStart('0');
                    var yDigits = y.AsSpan(yStart, j - yStart).TrimStart('0');
                    var order = xDigits.Length != yDigits.Length
                        ? xDigits.Length.CompareTo(yDigits.Length)
                        : xDigits.SequenceCompareTo(yDigits);
                    if (order != 0)
                        return order;

                    continue;
                }

                var a = ignoreCase ? char.ToUpperInvariant(x[i]) : x[i];
                var b = ignoreCase ? char.ToUpperInvariant(y[j]) : y[j];
                if (a != b)
                    return a.CompareTo(b);

                i++;
                j++;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}

[NodeDefinition("List/Distinct", Description = "Removes duplicate items, keeping the first of each.")]
public sealed class ListDistinctNode : ListTransformNode
{
    private bool _ignoreCase;

    public bool IgnoreCase
    {
        get => _ignoreCase;
        set => Set(ref _ignoreCase, value);
    }

    protected override string[] Apply(string[] items) =>
        items.Distinct(IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
}

[NodeDefinition("List/Reverse", Description = "Reverses the order of a list.")]
public sealed class ListReverseNode : ListTransformNode
{
    protected override string[] Apply(string[] items) => items.Reverse().ToArray();
}

[NodeDefinition("List/Filter", Description = "Keeps the items that contain, start with, end with, equal or match (regex) a value.")]
public sealed class ListFilterNode : ListTransformNode
{
    private string _find = string.Empty;
    private TextMatchMode _mode;
    private bool _ignoreCase;
    private bool _invert;

    private readonly InputPort _findIn;

    public ListFilterNode() => _findIn = AddInput<string>("Find");

    public string Find
    {
        get => _find;
        set => Set(ref _find, value ?? string.Empty);
    }

    public TextMatchMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    public bool IgnoreCase
    {
        get => _ignoreCase;
        set => Set(ref _ignoreCase, value);
    }

    /// <summary>Keep the items that do not match instead.</summary>
    public bool Invert
    {
        get => _invert;
        set => Set(ref _invert, value);
    }

    protected override string[] Apply(string[] items)
    {
        var find = _findIn.ValueOr(Find);

        // Filtering on nothing keeps everything, rather than matching everything or nothing depending
        // on the mode.
        if (find.Length == 0)
            return items;

        var matcher = TextMatch.Create(find, Mode, IgnoreCase);
        return items.Where(item => matcher(item) != Invert).ToArray();
    }
}

[NodeDefinition("List/Slice", Description = "A run of items from Start, Length long; a negative Start counts from the end.")]
public sealed class ListSliceNode : ListTransformNode
{
    private double _start;
    private double _length = -1;

    private readonly InputPort _startIn;
    private readonly InputPort _lengthIn;

    public ListSliceNode()
    {
        _startIn = AddInput<double>("Start");
        _lengthIn = AddInput<double>("Length");
    }

    public double Start
    {
        get => _start;
        set => Set(ref _start, value);
    }

    /// <summary>How many items to take; negative means all the rest.</summary>
    public double Length
    {
        get => _length;
        set => Set(ref _length, value);
    }

    protected override string[] Apply(string[] items)
    {
        var start = _startIn.ValueOr(Start);
        var length = _lengthIn.ValueOr(Length);

        var from = double.IsNaN(start) ? 0 : (int)Math.Clamp(start < 0 ? items.Length + start : start, 0, items.Length);
        var count = double.IsNaN(length) || length < 0 ? items.Length - from : (int)Math.Min(length, items.Length - from);
        return items[from..(from + count)];
    }
}

internal static class ListIndex
{
    public static (string? Item, bool Found) At(string[] items, double index)
    {
        if (double.IsNaN(index))
            return (null, false);

        var at = index < 0 ? items.Length + Math.Truncate(index) : Math.Truncate(index);
        return at >= 0 && at < items.Length ? (items[(int)at], true) : (null, false);
    }
}
