using vNoder.Core;

namespace vNoder.Nodes;

// More operations on text lists, in the same spirit as ListNodes.cs: out-of-range sizes clamp, and
// "not there" is a result (Found = false), not an error.

[NodeDefinition("List/Index Of", Description = "Whether a list contains an item, and its index (-1 when it does not).")]
public sealed class ListIndexOfNode : ListNode
{
    private string _find = string.Empty;
    private bool _ignoreCase;

    private readonly InputPort _findIn;
    private readonly OutputPort _foundOut;
    private readonly OutputPort _indexOut;
    private readonly OutputPort _countOut;

    public ListIndexOfNode()
    {
        _findIn = AddInput<string>("Find");
        _foundOut = AddOutput<bool>("Found");
        _indexOut = AddOutput<double>("Index");
        _countOut = AddOutput<double>("Count");
    }

    public string Find { get => _find; set => Set(ref _find, value ?? string.Empty); }

    public bool IgnoreCase { get => _ignoreCase; set => Set(ref _ignoreCase, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var find = _findIn.ValueOr(Find);
        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var items = Items;

        var index = Array.FindIndex(items, item => string.Equals(item, find, comparison));
        _foundOut.Value = index >= 0;
        _indexOut.Value = (double)index;
        _countOut.Value = (double)items.Count(item => string.Equals(item, find, comparison));
        return Task.CompletedTask;
    }
}

[NodeDefinition("List/Append", Description = "Joins two lists end to end, and optionally adds one more item.")]
public sealed class ListAppendNode : ListNode
{
    private readonly InputPort _moreIn;
    private readonly InputPort _itemIn;
    private readonly OutputPort _out;
    private readonly OutputPort _countOut;

    public ListAppendNode()
    {
        _moreIn = AddInput<string[]>("More");
        _itemIn = AddInput<string>("Item");
        _out = AddOutput<string[]>("Items");
        _countOut = AddOutput<double>("Count");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        string[] result = [.. Items, .. _moreIn.ValueOr(Array.Empty<string>())];
        if (_itemIn.Value is string item)
            result = [.. result, item];

        _out.Value = result;
        _countOut.Value = (double)result.Length;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Pairs two lists item by item, stopping at the shorter one. The pairs come out both as a list of
/// two-item lists, which a per-item node peels one pair at a time, and as text joined with a
/// separator, which is often all that is wanted ("name=value").
/// </summary>
[NodeDefinition("List/Zip", Description = "Pairs two lists item by item: as a list of pairs, and as text like name=value.")]
public sealed class ListZipNode : NodeBase
{
    private string _separator = "=";

    private readonly InputPort _aIn;
    private readonly InputPort _bIn;
    private readonly OutputPort _pairsOut;
    private readonly OutputPort _joinedOut;

    public ListZipNode()
    {
        _aIn = AddInput<string[]>("A");
        _bIn = AddInput<string[]>("B");
        _pairsOut = AddOutput<string[][]>("Pairs");
        _joinedOut = AddOutput<string[]>("Joined");
    }

    public string Separator { get => _separator; set => Set(ref _separator, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var pairs = _aIn.ValueOr(Array.Empty<string>())
            .Zip(_bIn.ValueOr(Array.Empty<string>()), (a, b) => new[] { a, b })
            .ToArray();

        _pairsOut.Value = pairs;
        _joinedOut.Value = pairs.Select(pair => pair[0] + Separator + pair[1]).ToArray();
        return Task.CompletedTask;
    }
}

[NodeDefinition("List/Chunk", Description = "Splits a list into lists of a given size, e.g. batches of 100; the last may be shorter.")]
public sealed class ListChunkNode : ListNode
{
    private double _chunkSize = 10;

    private readonly InputPort _sizeIn;
    private readonly OutputPort _out;
    private readonly OutputPort _countOut;

    public ListChunkNode()
    {
        _sizeIn = AddInput<double>("Size");
        _out = AddOutput<string[][]>("Chunks");
        _countOut = AddOutput<double>("Count");
    }

    // Not "Size": that is the node's own size on the canvas, inherited from NodeBase.
    public double ChunkSize { get => _chunkSize; set => Set(ref _chunkSize, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var size = _sizeIn.ValueOr(ChunkSize);
        var chunks = Items.Chunk(double.IsNaN(size) ? 1 : (int)Math.Clamp(size, 1, int.MaxValue)).ToArray();

        _out.Value = chunks;
        _countOut.Value = (double)chunks.Length;
        return Task.CompletedTask;
    }
}

[NodeDefinition("List/Shuffle", Description = "Puts a list in random order, different on every pass.")]
public sealed class ListShuffleNode : ListTransformNode
{
    protected override string[] Apply(string[] items)
    {
        var shuffled = items.ToArray();
        Random.Shared.Shuffle(shuffled);
        return shuffled;
    }
}

[NodeDefinition("List/Random Pick", Description = "Picks random items from a list, without picking any item twice.")]
public sealed class ListRandomPickNode : ListNode
{
    private double _count = 1;

    private readonly InputPort _countIn;
    private readonly OutputPort _itemOut;
    private readonly OutputPort _itemsOut;
    private readonly OutputPort _foundOut;

    public ListRandomPickNode()
    {
        _countIn = AddInput<double>("Count");
        _itemOut = AddOutput<string>("Item");
        _itemsOut = AddOutput<string[]>("Items");
        _foundOut = AddOutput<bool>("Found");
    }

    /// <summary>How many to pick; clamped to the list's length.</summary>
    public double Count { get => _count; set => Set(ref _count, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var items = Items.ToArray();
        var count = _countIn.ValueOr(Count);
        var take = double.IsNaN(count) ? 0 : (int)Math.Clamp(count, 0, items.Length);

        Random.Shared.Shuffle(items);
        var picked = items[..take];

        _itemsOut.Value = picked;
        _itemOut.Value = picked.Length > 0 ? picked[0] : null;
        _foundOut.Value = picked.Length > 0;
        return Task.CompletedTask;
    }
}

[NodeDefinition("List/Flatten", Description = "Joins a list of lists (from Chunk, Zip or a per-item run) into one list.")]
public sealed class ListFlattenNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;
    private readonly OutputPort _countOut;

    public ListFlattenNode()
    {
        _in = AddInput<string[][]>("Lists");
        _out = AddOutput<string[]>("Items");
        _countOut = AddOutput<double>("Count");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // A per-item run whose items were sometimes empty gives a list of "any" rather than of text
        // lists, so read any shape of nesting, item by item.
        var items = NodeValues.Items(_in.Value)
            .SelectMany(inner => NodeValues.Items(inner).Select(NodeValues.ToText))
            .ToArray();

        _out.Value = items;
        _countOut.Value = (double)items.Length;
        return Task.CompletedTask;
    }
}
