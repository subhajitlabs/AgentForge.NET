using System.Globalization;
using System.Text.Json;

namespace AgentForge.Tools;

internal sealed class SchemaValidator
{
    private readonly JsonElement _schema;
    private readonly int _maximumDepth;

    public SchemaValidator(JsonElement schema, int maximumDepth)
    {
        _schema = schema.Clone();
        _maximumDepth = maximumDepth;
    }

    public void Validate(JsonElement value)
    {
        try
        {
            ValidateNode(value, _schema, 1);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ArgumentException("Tool arguments do not satisfy the registered JSON contract.", nameof(value), exception);
        }
    }

    private void ValidateNode(JsonElement value, JsonElement schema, int depth)
    {
        if (depth > _maximumDepth)
            Invalid();

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            foreach (var branch in anyOf.EnumerateArray())
            {
                try
                {
                    ValidateNode(value, branch, depth);
                    return;
                }
                catch (ArgumentException)
                {
                }
            }
            Invalid();
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
            Invalid();

        if (schema.TryGetProperty("type", out var typeNode) && !MatchesType(value, typeNode))
            Invalid();

        if (value.ValueKind == JsonValueKind.Object)
            ValidateObject(value, schema, depth);
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray())
                ValidateNode(item, items, depth + 1);
    }

    private void ValidateObject(JsonElement value, JsonElement schema, int depth)
    {
        var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;
        var required = schema.TryGetProperty("required", out var requiredNode)
            ? requiredNode.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var name in required)
            if (!value.TryGetProperty(name, out _))
                Invalid();

        foreach (var property in value.EnumerateObject())
        {
            var propertySchema = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var matched)
                ? matched
                : default;
            if (propertySchema.ValueKind != JsonValueKind.Undefined)
                ValidateNode(property.Value, propertySchema, depth + 1);
            else
                Invalid();
        }
    }

    private static bool MatchesType(JsonElement value, JsonElement typeNode)
    {
        if (typeNode.ValueKind == JsonValueKind.Array)
            return typeNode.EnumerateArray().Any(item => MatchesType(value, item));
        var type = typeNode.GetString();
        return type switch
        {
            "null" => value.ValueKind == JsonValueKind.Null,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
            "number" => value.ValueKind == JsonValueKind.Number,
            _ => false
        };
    }

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
        "number" => value.ValueKind == JsonValueKind.Number,
        _ => false
    };

    private static bool IsInteger(JsonElement value) =>
        decimal.TryParse(value.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static void Invalid() =>
        throw new ArgumentException("Tool arguments do not satisfy the registered JSON contract.", "arguments");
}
