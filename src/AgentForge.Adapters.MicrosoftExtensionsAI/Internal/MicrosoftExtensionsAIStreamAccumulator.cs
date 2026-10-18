using System.Text;
using AgentForge.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentForge.Adapters.MicrosoftExtensionsAI.Internal;

internal sealed class MicrosoftExtensionsAIStreamAccumulator
{
    private readonly List<ChatResponseUpdate> _updates = [];
    private readonly StringBuilder _text = new();
    private ChatFinishReason? _finishReason;

    public IReadOnlyList<ModelTextDelta> Accept(ChatResponseUpdate? update)
    {
        if (update?.Contents is null)
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client stream returned an invalid update.");

        if (_finishReason is not null)
        {
            if (update.FinishReason is not null || update.Contents.Any(content => content is not UsageContent))
                throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                    "The chat client stream emitted content after terminal completion.");
        }
        else if (update.FinishReason is ChatFinishReason finishReason)
        {
            _finishReason = finishReason;
        }

        var deltas = new List<ModelTextDelta>();
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    _text.Append(text.Text);
                    deltas.Add(new ModelTextDelta(text.Text));
                    break;
                case FunctionCallContent:
                case UsageContent:
                    break;
                default:
                    throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                        "The chat client stream returned an unsupported content type.");
            }
        }
        _updates.Add(update.Clone());
        return deltas;
    }

    public ModelResponse Complete()
    {
        if (_finishReason is null)
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client stream ended without a finish reason.");
        ChatResponse response;
        try { response = _updates.ToChatResponse(); }
        catch (Exception exception)
        {
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client stream could not be reconstructed.", exception);
        }
        var mapped = MicrosoftExtensionsAIResponseMapper.Map(response);
        if (_text.Length > 0 && !StringComparer.Ordinal.Equals(_text.ToString(), mapped.Message.Text))
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The reconstructed response contradicts streamed text.");
        return mapped;
    }
}
