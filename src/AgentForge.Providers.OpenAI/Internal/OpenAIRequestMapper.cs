using AgentForge.Abstractions;
using OpenAI.Responses;

namespace AgentForge.Providers.OpenAI.Internal;

internal static class OpenAIRequestMapper
{
    public static CreateResponseOptions Map(ModelRequest request, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model.Value))
            throw new ArgumentException("The model identifier cannot be empty.", nameof(request));

        var options = new CreateResponseOptions
        {
            Model = request.Model.Value,
            StoredOutputEnabled = false,
            ParallelToolCallsEnabled = false,
            StreamingEnabled = streaming
        };

        foreach (var message in request.Messages)
        {
            switch (message)
            {
                case SystemMessage system:
                    options.InputItems.Add(ResponseItem.CreateSystemMessageItem(system.Text));
                    break;
                case UserMessage user:
                    options.InputItems.Add(ResponseItem.CreateUserMessageItem(user.Text));
                    break;
                case AssistantMessage { ToolCalls.Count: 0, Text: not null } assistant:
                    options.InputItems.Add(ResponseItem.CreateAssistantMessageItem(assistant.Text));
                    break;
                case AssistantMessage assistant:
                    foreach (var call in assistant.ToolCalls)
                        options.InputItems.Add(new FunctionCallResponseItem(
                            call.Id, call.Name, BinaryData.FromString(call.Arguments.GetRawText())));
                    break;
                case ToolResultMessage result:
                    options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(
                        result.ToolCallId, result.Result.GetRawText()));
                    break;
                default:
                    throw OpenAIExceptionMapper.Protocol("The model request contains an unsupported message type.");
            }
        }

        foreach (var tool in request.AvailableTools)
        {
            if (!IsValidFunctionName(tool.Name))
            {
                throw new ModelProviderException(
                    ModelProviderErrorKind.InvalidRequest,
                    false,
                    "An available tool name is not supported by the OpenAI provider.");
            }

            options.Tools.Add(ResponseTool.CreateFunctionTool(
                tool.Name,
                BinaryData.FromString(tool.ParametersSchema.GetRawText()),
                strictModeEnabled: false,
                functionDescription: tool.Description));
        }

        return options;
    }

    private static bool IsValidFunctionName(string name) =>
        name.Length is > 0 and <= 64
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
