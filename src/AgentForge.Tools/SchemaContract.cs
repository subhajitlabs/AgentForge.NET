using System.Text.Json.Nodes;

namespace AgentForge.Tools;

internal static class SchemaContract
{
    public static void CloseObjects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (RepresentsObject(obj))
                obj["additionalProperties"] = false;
            foreach (var value in obj.ToArray())
                CloseObjects(value.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
                CloseObjects(value);
        }
    }

    private static bool RepresentsObject(JsonObject obj)
    {
        if (obj.ContainsKey("properties"))
            return true;
        if (obj["type"] is JsonValue value && value.TryGetValue<string>(out var type))
            return type == "object";
        if (obj["type"] is JsonArray types)
            return types.Any(item => item?.GetValue<string>() == "object");
        return false;
    }
}
