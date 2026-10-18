using System.Collections;
using System.Text.Json.Serialization.Metadata;

namespace AgentForge.Tools;

internal static class ContractInspector
{
    public static void ValidateResult(JsonTypeInfo result)
    {
        var type = result.Type;
        if (type.ContainsGenericParameters || type == typeof(object) || typeof(Delegate).IsAssignableFrom(type) || typeof(Stream).IsAssignableFrom(type))
            throw new ArgumentException("The result contract contains an unsupported type.", "TResult");
    }

    public static void Validate(JsonTypeInfo root, int maximumDepth, int maximumProperties)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Kind != JsonTypeInfoKind.Object)
            throw Unsupported("The argument root must be a JSON object.");

        var path = new HashSet<Type>();
        var propertyCount = 0;
        Visit(root, 1, maximumDepth, maximumProperties, path, ref propertyCount);
    }

    private static void Visit(
        JsonTypeInfo typeInfo,
        int depth,
        int maximumDepth,
        int maximumProperties,
        HashSet<Type> path,
        ref int propertyCount)
    {
        if (depth > maximumDepth)
            throw Unsupported($"The argument contract exceeds the maximum depth of {maximumDepth}.");

        var type = Nullable.GetUnderlyingType(typeInfo.Type) ?? typeInfo.Type;
        if (type.ContainsGenericParameters || type == typeof(object) || typeof(Delegate).IsAssignableFrom(type) || typeof(Stream).IsAssignableFrom(type))
            throw Unsupported("The argument contract contains an unsupported type.");
        if (typeInfo.PolymorphismOptions is not null)
            throw Unsupported("Polymorphic argument contracts are not supported.");

        if (IsScalar(type))
            return;
        if (type.IsEnum)
        {
            if (!typeInfo.Options.Converters.Any(converter => converter is System.Text.Json.Serialization.JsonStringEnumConverter))
                throw Unsupported("Enums require JsonStringEnumConverter and are represented as strings.");
            return;
        }

        if (!path.Add(type))
            throw Unsupported("Reference cycles are not supported in tool argument contracts.");
        try
        {
            if (type.IsArray)
            {
                Visit(typeInfo.Options.GetTypeInfo(type.GetElementType()!), depth + 1, maximumDepth, maximumProperties, path, ref propertyCount);
                return;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                Visit(typeInfo.Options.GetTypeInfo(type.GetGenericArguments()[0]), depth + 1, maximumDepth, maximumProperties, path, ref propertyCount);
                return;
            }

            if (typeof(IDictionary).IsAssignableFrom(type) || ImplementsGenericDictionary(type))
                throw Unsupported("Dictionary argument contracts are not supported.");
            if (typeInfo.Kind != JsonTypeInfoKind.Object || (!type.IsValueType && !type.IsSealed))
                throw Unsupported("Argument objects must be sealed POCOs or records.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in typeInfo.Properties)
            {
                if (property.IsExtensionData)
                    throw Unsupported("Extension-data properties are not supported.");
                if (!names.Add(property.Name))
                    throw Unsupported("The argument contract contains duplicate JSON property names.");
                propertyCount++;
                if (propertyCount > maximumProperties)
                    throw Unsupported($"The argument contract exceeds the maximum of {maximumProperties} properties.");
                Visit(typeInfo.Options.GetTypeInfo(property.PropertyType), depth + 1, maximumDepth, maximumProperties, path, ref propertyCount);
            }
        }
        finally
        {
            path.Remove(type);
        }
    }

    private static bool IsScalar(Type type) =>
        type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double) ||
        type == typeof(decimal);

    private static bool ImplementsGenericDictionary(Type type) => type.GetInterfaces().Any(candidate =>
        candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));

    private static ArgumentException Unsupported(string message) => new(message, "TArguments");
}
