using System.Text.Json;
using AgentForge.Abstractions;
using OpenAI.Responses;

namespace AgentForge.Providers.OpenAI.Internal;

internal static class OpenAIResponseMapper
{
    public static ModelResponse Map(ResponseResult? result)
    {
        if (result?.Status is null)
            throw OpenAIExceptionMapper.Protocol("The provider response has no terminal status.");

        return result.Status.Value switch
        {
            ResponseStatus.Completed => MapCompleted(result),
            ResponseStatus.Incomplete => MapIncomplete(result),
            ResponseStatus.Cancelled => new ModelResponse(
                new AssistantMessage(null, []), ModelFinishReason.Cancelled, MapUsage(result.Usage)),
            ResponseStatus.Failed => throw OpenAIExceptionMapper.FromResponseError(result.Error),
            _ => throw OpenAIExceptionMapper.Protocol("The provider response is not terminal.")
        };
    }

    private static ModelResponse MapCompleted(ResponseResult result)
    {
        var text = new List<string>();
        var refusals = 0;
        var calls = new List<ToolCall>();
        foreach (var item in result.OutputItems)
        {
            switch (item)
            {
                case MessageResponseItem message:
                    if (message.Status is not null && message.Status != MessageStatus.Completed)
                        throw OpenAIExceptionMapper.Protocol("The provider returned an incomplete message item.");
                    foreach (var part in message.Content)
                    {
                        if (part.Kind == ResponseContentPartKind.OutputText)
                            text.Add(part.Text ?? throw OpenAIExceptionMapper.Protocol("The provider returned invalid output text."));
                        else if (part.Kind == ResponseContentPartKind.Refusal)
                            refusals++;
                        else
                            throw OpenAIExceptionMapper.Protocol("The provider returned an unsupported content type.");
                    }
                    break;
                case FunctionCallResponseItem call:
                    if (call.Status is not null && call.Status != FunctionCallStatus.Completed)
                        throw OpenAIExceptionMapper.Protocol("The provider returned an incomplete function call.");
                    if (string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.FunctionName))
                        throw OpenAIExceptionMapper.Protocol("The provider returned an invalid function call.");
                    calls.Add(new ToolCall(call.CallId, call.FunctionName, ParseArguments(call.FunctionArguments)));
                    break;
                default:
                    throw OpenAIExceptionMapper.Protocol("The provider returned an unsupported output item.");
            }
        }

        var combinedText = string.Concat(text);
        var hasText = !string.IsNullOrWhiteSpace(combinedText);
        if ((calls.Count > 0 ? 1 : 0) + (hasText ? 1 : 0) + (refusals > 0 ? 1 : 0) != 1)
            throw OpenAIExceptionMapper.Protocol("The provider returned contradictory output.");

        var usage = MapUsage(result.Usage);
        if (calls.Count > 0)
            return ModelResponse.ToolCalls(AssistantMessage.FromToolCalls([.. calls]), usage);
        if (hasText)
            return ModelResponse.Completed(AssistantMessage.FromText(combinedText), usage);
        return new ModelResponse(new AssistantMessage(null, []), ModelFinishReason.Refused, usage);
    }

    private static ModelResponse MapIncomplete(ResponseResult result)
    {
        var partialText = result.GetOutputText();
        var reason = result.IncompleteStatusDetails?.Reason;
        var finishReason = reason == ResponseIncompleteStatusReason.MaxOutputTokens
            ? ModelFinishReason.LengthLimit
            : reason == ResponseIncompleteStatusReason.ContentFilter
                ? ModelFinishReason.ContentFiltered
                : ModelFinishReason.Unknown;
        return new ModelResponse(new AssistantMessage(partialText, []), finishReason, MapUsage(result.Usage));
    }

    private static JsonElement ParseArguments(BinaryData? data)
    {
        if (data is null) throw OpenAIExceptionMapper.Protocol("The provider returned missing function arguments.");
        try
        {
            using var document = JsonDocument.Parse(data.ToMemory());
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw OpenAIExceptionMapper.Protocol("The provider returned malformed function arguments.", exception);
        }
    }

    private static ModelUsage? MapUsage(ResponseTokenUsage? usage)
    {
        if (usage is null) return null;
        if (usage.InputTokenCount < 0 || usage.OutputTokenCount < 0 || usage.TotalTokenCount < 0)
            throw OpenAIExceptionMapper.Protocol("The provider returned invalid usage values.");
        var total = checked((long)usage.InputTokenCount + usage.OutputTokenCount);
        if (total != usage.TotalTokenCount)
            throw OpenAIExceptionMapper.Protocol("The provider returned inconsistent usage values.");
        return new ModelUsage(usage.InputTokenCount, usage.OutputTokenCount);
    }
}
