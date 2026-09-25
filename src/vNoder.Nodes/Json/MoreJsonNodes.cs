using System.Text.Json.Nodes;
using vNoder.Core;

namespace vNoder.Nodes;

[NodeDefinition("JSON/Keys", Description = "The keys and values of a JSON object (or the indexes of an array), at a path.")]
public sealed class JsonKeysNode : NodeBase
{
    private string _path = string.Empty;

    private readonly InputPort _jsonIn;
    private readonly InputPort _pathIn;
    private readonly OutputPort _keysOut;
    private readonly OutputPort _valuesOut;
    private readonly OutputPort _countOut;
    private readonly OutputPort _foundOut;

    public JsonKeysNode()
    {
        _jsonIn = AddInput<string>("JSON");
        _pathIn = AddInput<string>("Path");
        _keysOut = AddOutput<string[]>("Keys");
        _valuesOut = AddOutput<string[]>("Values");
        _countOut = AddOutput<double>("Count");
        _foundOut = AddOutput<bool>("Found");
    }

    /// <summary>Where the object is; empty means the whole document.</summary>
    public string Path { get => _path; set => Set(ref _path, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var root = JsonText.Parse(_jsonIn.ValueOr(string.Empty));
        JsonPath.TryResolve(root, JsonPath.Parse(_pathIn.ValueOr(Path)), out var node);

        // Only an object or an array has keys; a missing path or a plain value is "not found".
        var (keys, values) = node switch
        {
            JsonObject obj => (obj.Select(p => p.Key).ToArray(), obj.Select(p => JsonText.ToText(p.Value)).ToArray()),
            JsonArray array => (array.Select((_, i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), array.Select(JsonText.ToText).ToArray()),
            _ => ((string[]?)null, (string[]?)null),
        };

        _keysOut.Value = keys ?? [];
        _valuesOut.Value = values ?? [];
        _countOut.Value = (double)(keys?.Length ?? 0);
        _foundOut.Value = keys is not null;
        return Task.CompletedTask;
    }
}

/// <summary>How <see cref="JsonMergeNode"/> combines two arrays at the same place.</summary>
public enum JsonArrayMerge
{
    Replace,
    Concat,
}

/// <summary>
/// Deep merge: objects are merged key by key, recursively, and anything else in B replaces what is in
/// A. The usual way to layer settings or patch a request body.
/// </summary>
[NodeDefinition("JSON/Merge", Description = "Deep-merges JSON B into A: objects key by key, B winning; arrays replaced or concatenated.")]
public sealed class JsonMergeNode : NodeBase
{
    private JsonArrayMerge _arrays;

    private readonly InputPort _aIn;
    private readonly InputPort _bIn;
    private readonly OutputPort _out;

    public JsonMergeNode()
    {
        _aIn = AddInput<string>("A");
        _bIn = AddInput<string>("B");
        _out = AddOutput<string>("JSON");
    }

    public JsonArrayMerge Arrays { get => _arrays; set => Set(ref _arrays, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var a = _aIn.ValueOr(string.Empty);
        var b = _bIn.ValueOr(string.Empty);

        // A side left empty merges nothing, so the node passes the other side through.
        var left = string.IsNullOrWhiteSpace(a) ? null : JsonText.Parse(a);
        var right = string.IsNullOrWhiteSpace(b) ? null : JsonText.Parse(b);
        var merged = string.IsNullOrWhiteSpace(b) ? left : string.IsNullOrWhiteSpace(a) ? right : Merge(left, right);

        _out.Value = merged?.ToJsonString(JsonText.Compact) ?? "null";
        return Task.CompletedTask;
    }

    private JsonNode? Merge(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (JsonObject left, JsonObject right):
                var result = (JsonObject)left.DeepClone();
                foreach (var (key, value) in right)
                    result[key] = result.TryGetPropertyValue(key, out var existing) ? Merge(existing, value) : value?.DeepClone();

                return result;

            case (JsonArray left, JsonArray right) when Arrays == JsonArrayMerge.Concat:
                return new JsonArray([.. left.Select(n => n?.DeepClone()), .. right.Select(n => n?.DeepClone())]);

            default:
                return b?.DeepClone();
        }
    }
}

/// <summary>
/// Builds a JSON array from a list, or an object when a list of keys is wired too (keys and values
/// paired by index). Items are strings unless they are numbers or booleans already, or Items Are JSON
/// is ticked, which nests each item as parsed JSON.
/// </summary>
[NodeDefinition("JSON/From List", Description = "Builds a JSON array from a list, or an object from lists of keys and values.")]
public sealed class JsonFromListNode : NodeBase
{
    private bool _itemsAreJson;

    private readonly InputPort _itemsIn;
    private readonly InputPort _keysIn;
    private readonly OutputPort _out;

    public JsonFromListNode()
    {
        _itemsIn = AddInput("Items", PortTypeCompatibility.Any);
        _keysIn = AddInput<string[]>("Keys");
        _out = AddOutput<string>("JSON");
    }

    public bool ItemsAreJson { get => _itemsAreJson; set => Set(ref _itemsAreJson, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var items = NodeValues.Items(_itemsIn.Value).Select(item => JsonText.FromValue(item, ItemsAreJson)).ToList();

        JsonNode result;
        if (_keysIn.Value is string[] keys)
        {
            if (keys.Length != items.Count)
                throw new InvalidOperationException($"{keys.Length} keys but {items.Count} values.");

            var obj = new JsonObject();
            for (var i = 0; i < keys.Length; i++)
                obj[keys[i]] = items[i];

            result = obj;
        }
        else
        {
            result = new JsonArray([.. items]);
        }

        _out.Value = result.ToJsonString(JsonText.Compact);
        return Task.CompletedTask;
    }
}
