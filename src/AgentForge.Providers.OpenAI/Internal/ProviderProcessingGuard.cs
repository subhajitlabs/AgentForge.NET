using System.Text;
using AgentForge.Abstractions;

namespace AgentForge.Providers.OpenAI.Internal;

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

    public static void Validate(ModelResponse response, AgentResourceLimits? limits)
    {
        var text = response.Message.Text;
        if (text is not null) _ = Add(0, text.Length, limits?.MaxProviderContentCharacters);
        var bytes = 0L;
        foreach (var call in response.Message.ToolCalls)
            bytes = Add(bytes, Encoding.UTF8.GetByteCount(call.Arguments.GetRawText()),
                limits?.MaxProviderToolArgumentBytes);
    }

    public static ModelProviderException Limit(Exception? innerException = null) =>
        new(ModelProviderErrorKind.ProviderProcessingLimitExceeded, false,
            "Provider processing exceeded a configured resource bound.", innerException);
}
