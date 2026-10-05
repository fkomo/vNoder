using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using vNoder.Core;

namespace vNoder.Nodes;

// XML and JSON converted both ways with the common convention (as in Json.NET and xml2js): the root
// element becomes the one property of the top object, attributes become "@name" properties, an
// element with both attributes or children and text keeps the text as "#text", and an element name
// that repeats becomes an array. Everything in XML is text, so values stay strings; To Number or JSON
// Get read them from there. Converting one way and back gives the same document, attribute order and
// comments aside.

/// <summary>
/// XML to JSON: RSS feeds, SOAP replies, config files and .csproj into something JSON Get can read by
/// path. Namespaces are dropped by default, as in XML Get. DTDs are refused.
/// </summary>
[NodeDefinition("Data/XML To JSON", Description = "Converts XML to JSON: attributes as @name, repeated elements as arrays.")]
public sealed class XmlToJsonNode : NodeBase
{
    private bool _ignoreNamespaces = true;

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public XmlToJsonNode()
    {
        _in = AddInput<string>("XML");
        _out = AddOutput<string>("JSON");
    }

    public bool IgnoreNamespaces { get => _ignoreNamespaces; set => Set(ref _ignoreNamespaces, value); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var document = XmlGetNode.Load(_in.ValueOr(string.Empty));
        if (IgnoreNamespaces)
            XmlGetNode.StripNamespaces(document);

        var root = document.Root ?? throw new InvalidOperationException("The XML has no root element.");
        var json = new JsonObject { [Name(root.Name)] = Convert(root) };
        _out.Value = json.ToJsonString(JsonText.Indented);
        return Task.CompletedTask;
    }

    private static JsonNode? Convert(XElement element)
    {
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        var children = element.Elements().ToList();
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();

        if (attributes.Count == 0 && children.Count == 0)
            return JsonValue.Create(text);

        var json = new JsonObject();
        foreach (var attribute in attributes)
            json["@" + Name(attribute.Name)] = attribute.Value;

        foreach (var group in children.GroupBy(c => Name(c.Name)))
        {
            var items = group.ToList();
            json[group.Key] = items.Count == 1
                ? Convert(items[0])
                : new JsonArray([.. items.Select(Convert)]);
        }

        if (text.Length > 0)
            json["#text"] = text;

        return json;
    }

    private static string Name(XName name) => name.Namespace == XNamespace.None ? name.LocalName : name.ToString();
}

/// <summary>
/// JSON to XML, the reverse of XML To JSON. A top object with a single property becomes that root
/// element; anything else is wrapped in Root Name. An array becomes repeated elements named after its
/// property (<c>item</c> at the top), and a name that is not valid in XML is encoded.
/// </summary>
[NodeDefinition("Data/JSON To XML", Description = "Converts JSON to XML: @name properties as attributes, arrays as repeated elements.")]
public sealed class JsonToXmlNode : NodeBase
{
    private string _rootName = "root";

    private readonly InputPort _in;
    private readonly OutputPort _out;

    public JsonToXmlNode()
    {
        _in = AddInput<string>("JSON");
        _out = AddOutput<string>("XML");
    }

    /// <summary>The element wrapped around JSON that is not a single-property object.</summary>
    public string RootName { get => _rootName; set => Set(ref _rootName, value ?? string.Empty); }

    public override Task EvaluateAsync(NodeEvaluationContext context)
    {
        var json = JsonText.Parse(_in.ValueOr(string.Empty));

        var root = json is JsonObject { Count: 1 } single
                   && single.First() is var (name, value)
                   && !name.StartsWith('@') && name != "#text"
                   && value is not JsonArray
            ? Element(name, value)
            : Element(RootName.Trim().Length > 0 ? RootName.Trim() : "root", json);

        _out.Value = root.ToString(SaveOptions.None);
        return Task.CompletedTask;
    }

    private static XElement Element(string name, JsonNode? value)
    {
        var element = new XElement(XmlConvert.EncodeLocalName(name));
        Fill(element, value);
        return element;
    }

    private static void Fill(XElement element, JsonNode? value)
    {
        switch (value)
        {
            case null:
                return;

            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (key.StartsWith('@') && key.Length > 1)
                        element.SetAttributeValue(XmlConvert.EncodeLocalName(key[1..]), Scalar(child));
                    else if (key == "#text")
                        element.Add(new XText(Scalar(child)));
                    else if (child is JsonArray items)
                        element.Add(items.Select(item => Element(key, item)));
                    else
                        element.Add(Element(key, child));
                }

                return;

            case JsonArray array:
                element.Add(array.Select(item => Element("item", item)));
                return;

            default:
                element.Add(new XText(Scalar(value)));
                return;
        }
    }

    /// <summary>A JSON value as element or attribute text: a string as itself, anything else as JSON.</summary>
    private static string Scalar(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => value.ToJsonString(),
    };
}
