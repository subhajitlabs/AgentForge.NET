using System.Text.Json;

namespace AgentForge.Abstractions;

internal static class JsonValueSnapshot
{
    public static JsonElement CloneOrUndefined(JsonElement value)
    {
        try
        {
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                return default;
            }

            return value.Clone();
        }
        catch (ObjectDisposedException)
        {
            return default;
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }
}
