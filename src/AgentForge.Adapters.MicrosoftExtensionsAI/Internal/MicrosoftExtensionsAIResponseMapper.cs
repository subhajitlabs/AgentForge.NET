using System.Text.Json;
using AgentForge.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentForge.Adapters.MicrosoftExtensionsAI.Internal;

internal static class MicrosoftExtensionsAIResponseMapper
{
    public static ModelResponse Map(ChatResponse? response)
    {
        if (response is null)
            throw MicrosoftExtensionsAIExceptionMapper.Protocol("The chat client returned no response.");
        if (response.Messages is null || response.Messages.Count != 1)
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client response must contain exactly one assistant message.");

        var message = response.Messages[0];
        if (message is null || message.Role != ChatRole.Assistant || message.Contents is null)
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client returned an invalid assistant message.");

        var text = new List<string>();
        var calls = new List<ToolCall>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent value when !string.IsNullOrEmpty(value.Text):
                    text.Add(value.Text);
                    break;
                case FunctionCallContent call:
                    if (call.Exception is not null || call.InformationalOnly
                        || string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.Name)
                        || call.Arguments is null || !callIds.Add(call.CallId))
                        throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                            "The chat client returned an invalid function call.");
                    calls.Add(new ToolCall(call.CallId, call.Name, SerializeArguments(call.Arguments)));
                    break;
                default:
                    throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                        "The chat client returned an unsupported content type.");
            }
        }

        var combinedText = string.Concat(text);
        var hasText = !string.IsNullOrWhiteSpace(combinedText);
        var usage = MapUsage(response.Usage);
        var finishReason = response.FinishReason;
        if (finishReason == ChatFinishReason.Stop)
        {
            if (!hasText || calls.Count > 0) throw Contradictory();
            return ModelResponse.Completed(AssistantMessage.FromText(combinedText), usage);
        }
        if (finishReason == ChatFinishReason.ToolCalls)
        {
            if (hasText || calls.Count == 0) throw Contradictory();
            return ModelResponse.ToolCalls(AssistantMessage.FromToolCalls([.. calls]), usage);
        }
        if (finishReason == ChatFinishReason.Length)
        {
            if (calls.Count > 0) throw Contradictory();
            return new ModelResponse(new AssistantMessage(hasText ? combinedText : null, []),
                ModelFinishReason.LengthLimit, usage);
        }
        if (finishReason == ChatFinishReason.ContentFilter)
        {
            if (calls.Count > 0) throw Contradictory();
            return new ModelResponse(new AssistantMessage(hasText ? combinedText : null, []),
                ModelFinishReason.ContentFiltered, usage);
        }
        if (calls.Count > 0)
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client returned function calls without a supported finish reason.");
        return new ModelResponse(new AssistantMessage(hasText ? combinedText : null, []),
            ModelFinishReason.Unknown, usage);
    }

    private static JsonElement SerializeArguments(IDictionary<string, object?> arguments)
    {
        try { return JsonSerializer.SerializeToElement(arguments).Clone(); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client returned function arguments that cannot be represented as JSON.", exception);
        }
    }

    private static ModelUsage? MapUsage(UsageDetails? usage)
    {
        if (usage is null) return null;
        if (usage.InputTokenCount is null && usage.OutputTokenCount is null && usage.TotalTokenCount is null)
            return null;
        if (usage.InputTokenCount is not long input || usage.OutputTokenCount is not long output
            || input < 0 || output < 0 || usage.TotalTokenCount is < 0)
            throw MicrosoftExtensionsAIExceptionMapper.InvalidUsage();
        long total;
        try { total = checked(input + output); }
        catch (OverflowException exception)
        {
            throw MicrosoftExtensionsAIExceptionMapper.InvalidUsage(exception);
        }
        if (usage.TotalTokenCount is long suppliedTotal && suppliedTotal != total)
            throw MicrosoftExtensionsAIExceptionMapper.InvalidUsage();
        return new ModelUsage(input, output);
    }

    private static ModelProviderException Contradictory() =>
        MicrosoftExtensionsAIExceptionMapper.Protocol(
            "The chat client response contradicts its finish reason.");
}
