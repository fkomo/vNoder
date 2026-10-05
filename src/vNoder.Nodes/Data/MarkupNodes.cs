using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using vNoder.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace vNoder.Nodes;

/// <summary>
/// Reads XML with an XPath expression: <c>//item/title</c>, <c>/feed/entry[1]/@href</c>,
/// <c>count(//item)</c>. Namespaces are ignored by default, so the paths work on RSS, Atom, SOAP and
/// .csproj files without declaring prefixes. DTDs are refused, which also rules out entity tricks.
/// </summary>
/// <remarks>
/// Matching nothing is a result (Found = false), not an error; malformed XML or an invalid XPath
/// throws. An element with child elements reads as its XML; anything else as its text.
/// </remarks>
[NodeDefinition("Data/XML Get", Description = "Reads values from XML by XPath, e.g. //item/title or count(//item).")]
public sealed class XmlGetNode : NodeBase
{
    private string _xPath = string.Empty;
    private bool _ignoreNamespaces = true;

    private readonly InputPort _xmlIn;
    private readonly InputPort _xPathIn;
    private readonly OutputPort _valueOut;
    private readonly OutputPort _valuesOut;
    private readonly OutputPort _countOut;
    private readonly OutputPort _foundOut;

    public XmlGetNode()
    {
        _xmlIn = AddInput<string>("XML");
        _xPathIn = AddInput<string>("XPath");
        _valueOut = AddOutput<string>("Value");
        _valuesOut = AddOutput<string[]>("Values");
        _countOut = AddOutput<double>("Count");
        _foundOut = AddOutput<bool>("Found");
    }

    public string XPath { get => _xPath; set => Set(ref _xPath, value ?? string.Empty); }

    public bool IgnoreNamespaces { get => _ignoreNamespaces; set => Set(ref _ignoreNamespaces, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var document = Load(_xmlIn.ValueOr(string.Empty));
        if (IgnoreNamespaces)
            StripNamespaces(document);

        var path = _xPathIn.ValueOr(XPath).Trim();
        if (path.Length == 0)
            path = "/*";

        object result;
        try
        {
            result = document.CreateNavigator().Evaluate(path);
        }
        catch (XPathException exception)
        {
            throw new InvalidOperationException($"Invalid XPath: {exception.Message}");
        }

        var values = result switch
        {
            XPathNodeIterator nodes => nodes.Cast<XPathNavigator>().Select(Text).ToArray(),
            double number => [number.ToString("0.######", CultureInfo.InvariantCulture)],
            bool flag => [flag ? "true" : "false"],
            var other => [other.ToString() ?? string.Empty],
        };

        _valuesOut.Value = values;
        _valueOut.Value = values.Length > 0 ? values[0] : null;
        _countOut.Value = (double)values.Length;
        _foundOut.Value = values.Length > 0;
        return Task.CompletedTask;
    }

    internal static XDocument Load(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException("No XML to read.");

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidOperationException($"Invalid XML: {exception.Message}");
        }
    }

    internal static void StripNamespaces(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            element.Name = element.Name.LocalName;
            var attributes = element.Attributes()
                .Where(a => !a.IsNamespaceDeclaration)
                .Select(a => new XAttribute(a.Name.LocalName, a.Value))
                .ToList();
            element.ReplaceAttributes(attributes);
        }
    }

    private static string Text(XPathNavigator node) =>
        node.NodeType == XPathNodeType.Element && node.HasChildren && node.SelectChildren(XPathNodeType.Element).Count > 0
            ? node.OuterXml
            : node.Value;
}

/// <summary>
/// YAML's plain scalars read the way YAML 1.2's core schema reads them: null, booleans, integers
/// (including 0x and 0o) and floats. Everything else, and anything quoted, is a string.
/// </summary>
internal static partial class YamlScalars
{
    public static JsonNode? ToJson(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;
        if (scalar.Style is not (ScalarStyle.Plain or ScalarStyle.Any))
            return JsonValue.Create(text);

        return Resolve(text) switch
        {
            null => null,
            bool flag => JsonValue.Create(flag),
            double number => JsonValue.Create(number),
            _ => JsonValue.Create(text),
        };
    }

    /// <summary>What a plain scalar means: null, a bool, a finite double, or the text itself.</summary>
    public static object? Resolve(string text)
    {
        if (text is "" or "~" or "null" or "Null" or "NULL")
            return null;

        if (text is "true" or "True" or "TRUE")
            return true;

        if (text is "false" or "False" or "FALSE")
            return false;

        if (Integer().IsMatch(text))
            return double.Parse(text, CultureInfo.InvariantCulture);

        if (text.StartsWith("0x", StringComparison.Ordinal) && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return (double)hex;

        if (text.StartsWith("0o", StringComparison.Ordinal) && text.Length > 2 && text[2..].All(c => c is >= '0' and <= '7'))
            return (double)Convert.ToInt64(text[2..], 8);

        if (Float().IsMatch(text))
            return double.Parse(text, CultureInfo.InvariantCulture);

        // .inf and .nan are YAML numbers, but JSON has no way to write them, so they stay text.
        return text;
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex Integer();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();
}

/// <summary>
/// YAML to JSON: mappings become objects, sequences arrays, scalars typed by the YAML 1.2 core schema.
/// Anchors and aliases are resolved. Only the first document of a multi-document stream is read.
/// </summary>
[NodeDefinition("Data/YAML To JSON", Description = "Converts YAML (config files, Kubernetes, CI pipelines) to JSON.")]
public sealed class YamlToJsonNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public YamlToJsonNode()
    {
        _in = AddInput<string>("YAML");
        _out = AddOutput<string>("JSON");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(_in.ValueOr(string.Empty)));
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException($"Invalid YAML: {exception.Message}");
        }

        var root = stream.Documents.Count > 0 ? Convert(stream.Documents[0].RootNode) : null;
        _out.Value = root?.ToJsonString(JsonText.Compact) ?? "null";
        return Task.CompletedTask;
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair =>
            KeyValuePair.Create(((pair.Key as YamlScalarNode)?.Value) ?? pair.Key.ToString(), Convert(pair.Value)))),
        YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(Convert)]),
        YamlScalarNode scalar => YamlScalars.ToJson(scalar),
        _ => null,
    };
}

/// <summary>
/// JSON to YAML in block style. A string that YAML would otherwise read as something else ("true",
/// "123", "null", "") is quoted, so the YAML reads back as the same JSON; multi-line strings use a
/// literal block.
/// </summary>
[NodeDefinition("Data/JSON To YAML", Description = "Converts JSON to YAML.")]
public sealed class JsonToYamlNode : NodeBase
{
    private readonly InputPort _in;
    private readonly OutputPort _out;

    public JsonToYamlNode()
    {
        _in = AddInput<string>("JSON");
        _out = AddOutput<string>("YAML");
    }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var root = JsonText.Parse(_in.ValueOr(string.Empty));
        var stream = new YamlStream(new YamlDocument(Convert(root)));

        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);

        // The emitter closes the document with "...", which nobody wants in a config file.
        var yaml = writer.ToString().TrimEnd();
        if (yaml.EndsWith("...", StringComparison.Ordinal))
            yaml = yaml[..^3].TrimEnd();

        _out.Value = yaml + Environment.NewLine;
        return Task.CompletedTask;
    }

    private static YamlNode Convert(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var mapping = new YamlMappingNode();
                foreach (var (key, value) in obj)
                    mapping.Add(Scalar(key), Convert(value));

                return mapping;

            case JsonArray array:
                var sequence = new YamlSequenceNode();
                foreach (var item in array)
                    sequence.Add(Convert(item));

                return sequence;

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return Scalar(value.GetValue<string>());

            case null:
                return new YamlScalarNode("null") { Style = ScalarStyle.Plain };

            default:
                // Numbers and booleans as JSON writes them, which YAML reads the same way.
                return new YamlScalarNode(node.ToJsonString()) { Style = ScalarStyle.Plain };
        }
    }

    private static YamlScalarNode Scalar(string text) => new(text)
    {
        Style = text.Contains('\n') ? ScalarStyle.Literal
            : YamlScalars.Resolve(text) is string ? ScalarStyle.Any
            : ScalarStyle.DoubleQuoted,
    };
}
