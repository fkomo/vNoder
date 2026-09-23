using System.Text;

namespace vNoder.Nodes;

/// <summary>Conversions shared by nodes whose "any" input accepts either text or bytes.</summary>
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
}
