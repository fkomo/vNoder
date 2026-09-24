using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using vNoder.Core;

namespace vNoder.Nodes;

// JSON travels as text: there is no JSON port type, so the output of HTTP Request or File Read wires
// straight in, and the result of Set or Format wires straight into a request body or a file.

[NodeDefinition("JSON/Get", Description = "Reads a value from JSON by path, e.g. data.items[0].name.")]
public sealed class JsonGetNode : NodeBase
{
    private string _path = string.Empty;

    private readonly InputPort _jsonIn;
    private readonly InputPort _pathIn;
    private readonly OutputPort _valueOut;
    private readonly OutputPort _textOut;
    private readonly OutputPort _foundOut;

    public JsonGetNode()
    {
        _jsonIn = AddInput<string>("JSON");
        _pathIn = AddInput<string>("Path");
        _valueOut = AddOutput("Value", PortTypeCompatibility.Any);
        _textOut = AddOutput<string>("Text");
        _foundOut = AddOutput<bool>("Found");
    }

    /// <summary>Dotted path with [index] for arrays; empty means the whole document.</summary>
    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        _valueOut.Value = null;
        _textOut.Value = null;
        _foundOut.Value = false;

        var root = JsonText.Parse(_jsonIn.ValueOr(string.Empty));
        var segments = JsonPath.Parse(_pathIn.ValueOr(Path));

        // A path that leads nowhere is a result (Found = false), not an error: optional fields are
        // exactly what a Select or Coalesce downstream is for.
        if (!JsonPath.TryResolve(root, segments, out var node))
            return Task.CompletedTask;

        _valueOut.Value = JsonText.ToValue(node);
        _textOut.Value = JsonText.ToText(node);
        _foundOut.Value = true;
        return Task.CompletedTask;
    }
}

[NodeDefinition("JSON/Set", Description = "Sets a value in JSON by path, creating objects and arrays as needed; builds JSON from nothing.")]
public sealed class JsonSetNode : NodeBase
{
    private string _path = string.Empty;
    private bool _valueIsJson;

    private readonly InputPort _jsonIn;
    private readonly InputPort _pathIn;
    private readonly InputPort _valueIn;
    private readonly OutputPort _out;

    public JsonSetNode()
    {
        _jsonIn = AddInput<string>("JSON");
        _pathIn = AddInput<string>("Path");
        _valueIn = AddInput("Value", PortTypeCompatibility.Any);
        _out = AddOutput<string>("JSON");
    }

    public string Path
    {
        get => _path;
        set => Set(ref _path, value ?? string.Empty);
    }

    /// <summary>Read text as JSON rather than as a string, so an object from another node nests.</summary>
    public bool ValueIsJson
    {
        get => _valueIsJson;
        set => Set(ref _valueIsJson, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var text = _jsonIn.ValueOr(string.Empty);
        var root = string.IsNullOrWhiteSpace(text) ? null : JsonText.Parse(text);
        var value = JsonText.FromValue(_valueIn.Value, ValueIsJson);
        var segments = JsonPath.Parse(_pathIn.ValueOr(Path));

        root = JsonPath.Set(root, segments, value);
        _out.Value = root?.ToJsonString(JsonText.Compact) ?? "null";
        return Task.CompletedTask;
    }
}

public enum JsonStyle
{
    Indented,
    Minified,
}

[NodeDefinition("JSON/Format", Description = "Pretty-prints or minifies JSON, and checks that it is valid.")]
public sealed class JsonFormatNode : NodeBase
{
    private JsonStyle _style;

    private readonly InputPort _in;
    private readonly OutputPort _out;
    private readonly OutputPort _validOut;
    private readonly OutputPort _errorOut;

    public JsonFormatNode()
    {
        _in = AddInput<string>("JSON");
        _out = AddOutput<string>("JSON");
        _validOut = AddOutput<bool>("Valid");
        _errorOut = AddOutput<string>("Error");
    }

    public JsonStyle Style
    {
        get => _style;
        set => Set(ref _style, value);
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        // Invalid JSON is a result here, not a failure: this is the node that answers "is it valid?".
        try
        {
            var root = JsonText.Parse(_in.ValueOr(string.Empty));
            _out.Value = root?.ToJsonString(Style == JsonStyle.Indented ? JsonText.Indented : JsonText.Compact) ?? "null";
            _validOut.Value = true;
            _errorOut.Value = string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            _out.Value = null;
            _validOut.Value = false;
            _errorOut.Value = exception.Message;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Parsing, and moving values between JSON and the port types.</summary>
internal static class JsonText
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The document, or null for a literal <c>null</c>. Malformed JSON throws with the position.</summary>
    public static JsonNode? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No JSON to read.");

        try
        {
            return JsonNode.Parse(text, documentOptions: Lenient);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Invalid JSON: {exception.Message}");
        }
    }

    /// <summary>
    /// A JSON value as a port value: strings, numbers and booleans as themselves, an array as a text
    /// list (so it feeds the List nodes), an object as its JSON text.
    /// </summary>
    public static object? ToValue(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => array.Select(ToText).ToArray(),
        JsonObject => node.ToJsonString(Compact),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.GetValue<double>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        },
        _ => null,
    };

    /// <summary>A string unquoted, anything else as its JSON; null as empty.</summary>
    public static string ToText(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => node.ToJsonString(Compact),
    };

    public static JsonNode? FromValue(object? value, bool textIsJson) => value switch
    {
        null => null,
        string text when textIsJson => string.IsNullOrWhiteSpace(text) ? null : Parse(text),
        string text => JsonValue.Create(text),
        double number when !double.IsFinite(number) =>
            throw new InvalidOperationException($"{number.ToString(CultureInfo.InvariantCulture)} cannot be written as JSON."),
        double number => JsonValue.Create(number),
        bool flag => JsonValue.Create(flag),
        string[] parts => new JsonArray(parts.Select(p => textIsJson && !string.IsNullOrWhiteSpace(p) ? Parse(p) : JsonValue.Create(p)).ToArray()),
        DateTime moment => JsonValue.Create(moment.ToString("o", CultureInfo.InvariantCulture)),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        var other => JsonValue.Create(other.ToString()),
    };
}

/// <summary>
/// Paths like <c>data.items[0].name</c>. A leading <c>$</c> is allowed, <c>["odd.key"]</c> quotes a
/// key containing dots or brackets, and a negative index counts from the end of an array.
/// </summary>
internal static class JsonPath
{
    public static List<object> Parse(string path)
    {
        var segments = new List<object>();
        var span = path.Trim();
        var i = 0;

        if (span.StartsWith('$'))
            i = 1;

        while (i < span.Length)
        {
            var c = span[i];
            if (c == '.')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                var close = span.IndexOf(']', i);
                if (span.Length > i + 1 && span[i + 1] is '"' or '\'')
                {
                    var quote = span[i + 1];
                    var end = span.IndexOf(quote, i + 2);
                    if (end < 0 || end + 1 >= span.Length || span[end + 1] != ']')
                        throw new InvalidOperationException($"Unclosed quoted key in path '{path}'.");

                    segments.Add(span[(i + 2)..end]);
                    i = end + 2;
                    continue;
                }

                if (close < 0)
                    throw new InvalidOperationException($"Missing ']' in path '{path}'.");

                var inner = span[(i + 1)..close].Trim();
                if (!int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
                    throw new InvalidOperationException($"'[{inner}]' is not an array index; quote a key as [\"{inner}\"].");

                segments.Add(index);
                i = close + 1;
                continue;
            }

            var start = i;
            while (i < span.Length && span[i] is not ('.' or '['))
                i++;

            segments.Add(span[start..i].Trim());
        }

        return segments;
    }

    public static bool TryResolve(JsonNode? root, IReadOnlyList<object> segments, out JsonNode? node)
    {
        node = root;
        foreach (var segment in segments)
        {
            switch (segment, node)
            {
                case (string key, JsonObject obj) when obj.TryGetPropertyValue(key, out var child):
                    node = child;
                    break;

                case (int index, JsonArray array) when Normalise(index, array.Count) is var at && at >= 0 && at < array.Count:
                    node = array[at];
                    break;

                default:
                    node = null;
                    return false;
            }
        }

        return true;
    }

    /// <summary>Sets <paramref name="value"/> at the path under <paramref name="root"/>, returning the (possibly new) root.</summary>
    public static JsonNode? Set(JsonNode? root, IReadOnlyList<object> segments, JsonNode? value)
    {
        if (segments.Count == 0)
            return value;

        root ??= segments[0] is int ? new JsonArray() : new JsonObject();
        var container = root;

        for (var s = 0; s < segments.Count; s++)
        {
            var last = s == segments.Count - 1;
            var segment = segments[s];
            JsonNode? next = last ? value : null;

            switch (segment, container)
            {
                case (string key, JsonObject obj):
                    if (last)
                    {
                        obj[key] = value;
                        return root;
                    }

                    next = obj[key];
                    if (next is not (JsonObject or JsonArray))
                    {
                        next = segments[s + 1] is int ? new JsonArray() : new JsonObject();
                        obj[key] = next;
                    }

                    break;

                case (int index, JsonArray array):
                    var at = Normalise(index, array.Count);
                    if (at < 0 || at > array.Count)
                        throw new InvalidOperationException($"Index {index} is outside an array of {array.Count}; use {array.Count} to append.");

                    if (last)
                    {
                        if (at == array.Count)
                            array.Add(value);
                        else
                            array[at] = value;

                        return root;
                    }

                    next = at < array.Count ? array[at] : null;
                    if (next is not (JsonObject or JsonArray))
                    {
                        next = segments[s + 1] is int ? new JsonArray() : new JsonObject();
                        if (at == array.Count)
                            array.Add(next);
                        else
                            array[at] = next;
                    }

                    break;

                default:
                    throw new InvalidOperationException(segment is int
                        ? $"'{Describe(segments, s)}' is not an array."
                        : $"'{Describe(segments, s)}' is not an object.");
            }

            container = next!;
        }

        return root;
    }

    private static int Normalise(int index, int count) => index < 0 ? count + index : index;

    private static string Describe(IReadOnlyList<object> segments, int upTo)
    {
        var text = new StringBuilder("$");
        for (var i = 0; i < upTo; i++)
            text.Append(segments[i] is int n ? $"[{n}]" : $".{segments[i]}");

        return text.ToString();
    }
}
