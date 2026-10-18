using System.Text.Json;
using AgentForge.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentForge.Adapters.MicrosoftExtensionsAI.Internal;

internal static class MicrosoftExtensionsAIRequestMapper
{
    public static (IReadOnlyList<ChatMessage> Messages, ChatOptions Options) Map(ModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model.Value))
            throw new ArgumentException("The model identifier cannot be empty.", nameof(request));

        var messages = new List<ChatMessage>(request.Messages.Count);
        foreach (var message in request.Messages)
        {
            switch (message)
            {
                case SystemMessage system:
                    messages.Add(new ChatMessage(ChatRole.System, system.Text));
                    break;
                case UserMessage user:
                    messages.Add(new ChatMessage(ChatRole.User, user.Text));
                    break;
                case AssistantMessage { ToolCalls.Count: 0, Text: not null } assistant:
                    messages.Add(new ChatMessage(ChatRole.Assistant, assistant.Text));
                    break;
                case AssistantMessage assistant:
                    messages.Add(new ChatMessage(ChatRole.Assistant,
                        assistant.ToolCalls.Select(MapFunctionCall).Cast<AIContent>().ToList()));
                    break;
                case ToolResultMessage result:
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(result.ToolCallId, result.Result.Clone())]));
                    break;
                default:
                    throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                        "The model request contains an unsupported message type.");
            }
        }

        var tools = request.AvailableTools
            .Select(tool => (AITool)AIFunctionFactory.CreateDeclaration(
                tool.Name, tool.Description, tool.ParametersSchema.Clone(), returnJsonSchema: null))
            .ToList();
        return (messages.AsReadOnly(), new ChatOptions { ModelId = request.Model.Value, Tools = tools });
    }

    private static FunctionCallContent MapFunctionCall(ToolCall call)
    {
        if (call.Arguments.ValueKind != JsonValueKind.Object)
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "A function call requires a JSON object argument value.");
        try
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments.GetRawText())
                ?? throw new JsonException();
            return new FunctionCallContent(call.Id, call.Name, arguments!);
        }
        catch (JsonException exception)
        {
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "A function call contains malformed arguments.", exception);
        }
    }
}
