using System.Reflection;

namespace vNoder.Core;

/// <summary>
/// Discovers a node's user-editable state by reflection: the public read/write properties it declares
/// itself, of a type that round-trips through JSON.
/// </summary>
/// <remarks>
/// Persistence is deliberately generic so that adding a node type stays a one-class operation. A new
/// node's inline properties are saved and loaded with no serializer changes and nothing to register.
/// Properties that evaluation computes (<c>OutputNode.Value</c>, <c>LengthNode.Count</c>) are excluded
/// by having a non-public setter, and <see cref="NodeBase.Position"/> by being declared on the base.
/// </remarks>
public static class NodeStateProperties
{
    private static readonly Dictionary<Type, PropertyInfo[]> Cache = [];

    public static IReadOnlyList<PropertyInfo> For(Type nodeType)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(nodeType, out var cached))
                return cached;

            var properties = nodeType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead
                            && p.CanWrite
                            && p.GetMethod is { IsPublic: true }
                            && p.SetMethod is { IsPublic: true }
                            && p.GetIndexParameters().Length == 0
                            && p.DeclaringType != typeof(NodeBase)
                            && IsPersistable(p.PropertyType))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToArray();

            Cache[nodeType] = properties;
            return properties;
        }
    }

    private static bool IsPersistable(Type type)
    {
        var effective = Nullable.GetUnderlyingType(type) ?? type;

        return effective.IsEnum
               || effective == typeof(string)
               || effective == typeof(bool)
               || effective == typeof(double)
               || effective == typeof(float)
               || effective == typeof(decimal)
               || effective == typeof(int)
               || effective == typeof(long);
    }
}
