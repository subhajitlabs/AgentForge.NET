using System.Text;
using System.Text.Json;
using AgentForge.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentForge.Adapters.MicrosoftExtensionsAI.Internal;

internal static class ProviderProcessingGuard
{
    public static long Add(long current, long addition, long? maximum)
    {
        long total;
        try { total = checked(current + addition); }
        catch (OverflowException exception) { throw Limit(exception); }
        if (maximum is { } limit && total > limit) throw Limit();
        return total;
    }

    public static long GetArgumentBytes(FunctionCallContent call)
    {
        try { return JsonSerializer.SerializeToUtf8Bytes(call.Arguments).LongLength; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client returned function arguments that cannot be represented as JSON.", exception);
        }
    }

    public static void Validate(ModelResponse response, AgentResourceLimits? limits)
    {
        if (response.Message.Text is { } text)
            _ = Add(0, text.Length, limits?.MaxProviderContentCharacters);
        var bytes = 0L;
        foreach (var call in response.Message.ToolCalls)
            bytes = Add(bytes, Encoding.UTF8.GetByteCount(call.Arguments.GetRawText()),
                limits?.MaxProviderToolArgumentBytes);
    }

    public static ModelProviderException Limit(Exception? innerException = null) =>
        new(ModelProviderErrorKind.ProviderProcessingLimitExceeded, false,
            "Provider processing exceeded a configured resource bound.", innerException);
}
