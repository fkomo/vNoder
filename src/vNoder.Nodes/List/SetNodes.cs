using vNoder.Core;

namespace vNoder.Nodes;

/// <summary>Set operations offered by <see cref="SetOperationNode"/>. Saved by number: append only.</summary>
public enum SetOperation
{
    /// <summary>Every item of A or B.</summary>
    Union,

    /// <summary>The items of A that are also in B.</summary>
    Intersect,

    /// <summary>The items of A that are not in B: what is new, or what went missing.</summary>
    Except,

    /// <summary>The items in one list but not both: what changed between them.</summary>
    SymmetricDifference,
}

/// <summary>
/// Union, intersection and differences of two text lists. One node with an operation selector rather
/// than one per operation: they take the same two lists, and switching should not mean rewiring.
/// </summary>
/// <remarks>
/// Results keep the order items first appear in (A, then B) and hold each item once. With History or
/// Previous Value, Except gives what appeared since the last pass: this pass's list except the last one.
/// </remarks>
[NodeDefinition("List/Set Operation", Description = "Union, intersect, except or symmetric difference of two lists — what is in both, new, or missing.")]
public sealed class SetOperationNode : NodeBase
{
    private SetOperation _operation = SetOperation.Except;
    private bool _ignoreCase;

    private readonly InputPort _aIn;
    private readonly InputPort _bIn;
    private readonly OutputPort _out;
    private readonly OutputPort _countOut;

    public SetOperationNode()
    {
        _aIn = AddInput("A", PortTypeCompatibility.ItemList);
        _bIn = AddInput("B", PortTypeCompatibility.ItemList);
        _out = AddOutput<string[]>("Items");
        _countOut = AddOutput<double>("Count");
    }

    public SetOperation Operation { get => _operation; set => Set(ref _operation, value); }

    public bool IgnoreCase { get => _ignoreCase; set => Set(ref _ignoreCase, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var a = NodeValues.TextItems(_aIn.Value);
        var b = NodeValues.TextItems(_bIn.Value);
        var comparer = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        string[] result = Operation switch
        {
            SetOperation.Union => [.. a.Union(b, comparer)],
            SetOperation.Intersect => [.. a.Intersect(b, comparer)],
            SetOperation.SymmetricDifference => [.. a.Except(b, comparer).Concat(b.Except(a, comparer))],
            _ => [.. a.Except(b, comparer)],
        };

        _out.Value = result;
        _countOut.Value = (double)result.Length;
        return Task.CompletedTask;
    }
}

/// <summary>How <see cref="CountByNode"/> orders its rows. Saved by number: append only.</summary>
public enum CountByOrder
{
    /// <summary>Most frequent first; ties in the order first seen.</summary>
    MostFrequent,

    /// <summary>By the item itself, naturally (so "item 2" before "item 10").</summary>
    Alphabetical,

    /// <summary>In the order each item first appears.</summary>
    FirstSeen,
}

/// <summary>
/// How often each distinct item occurs: a frequency table of words, status codes, log levels or file
/// extensions. Keys and Counts line up by index; Rows is the same as a list of [item, count] pairs with
/// a header, ready for Table or CSV Format.
/// </summary>
[NodeDefinition("List/Count By", Description = "How many times each distinct item occurs — a frequency table, most frequent first.")]
public sealed class CountByNode : ListNode
{
    private CountByOrder _order;
    private bool _ignoreCase;

    private readonly OutputPort _keysOut;
    private readonly OutputPort _countsOut;
    private readonly OutputPort _rowsOut;
    private readonly OutputPort _distinctOut;

    public CountByNode()
    {
        _keysOut = AddOutput<string[]>("Keys");
        _countsOut = AddOutput<double[]>("Counts");
        _rowsOut = AddOutput<string[][]>("Rows");
        _distinctOut = AddOutput<double>("Distinct");
    }

    public CountByOrder Order { get => _order; set => Set(ref _order, value); }

    public bool IgnoreCase { get => _ignoreCase; set => Set(ref _ignoreCase, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var comparer = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // GroupBy keeps first-seen order, and OrderBy is stable, so ties keep it too.
        var groups = Items.GroupBy(item => item, comparer).Select(g => (Key: g.Key, Count: g.Count()));
        groups = Order switch
        {
            CountByOrder.MostFrequent => groups.OrderByDescending(g => g.Count),
            CountByOrder.Alphabetical => groups.OrderBy(g => g.Key, new ListSortNode.NaturalComparer(IgnoreCase)),
            _ => groups,
        };

        var table = groups.ToList();
        _keysOut.Value = table.Select(g => g.Key).ToArray();
        _countsOut.Value = table.Select(g => (double)g.Count).ToArray();
        _rowsOut.Value = new[] { new[] { "Item", "Count" } }
            .Concat(table.Select(g => new[] { g.Key, g.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
            .ToArray();
        _distinctOut.Value = (double)table.Count;
        return Task.CompletedTask;
    }
}
