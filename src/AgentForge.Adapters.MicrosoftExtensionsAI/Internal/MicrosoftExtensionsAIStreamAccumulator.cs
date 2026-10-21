using System.Text;
using AgentForge.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentForge.Adapters.MicrosoftExtensionsAI.Internal;

internal sealed class MicrosoftExtensionsAIStreamAccumulator
{
    private readonly AgentResourceLimits? _limits;
    private readonly List<ChatResponseUpdate> _updates = [];
    private readonly StringBuilder _text = new();
    private ChatFinishReason? _finishReason;
    private ChatResponseUpdate? _usageUpdate;
    private long _textCharacters;
    private long _argumentBytes;

    public MicrosoftExtensionsAIStreamAccumulator(AgentResourceLimits? limits) => _limits = limits;

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
        var usageContents = 0;
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    _textCharacters = ProviderProcessingGuard.Add(
                        _textCharacters, text.Text.Length, _limits?.MaxProviderContentCharacters);
                    _text.Append(text.Text);
                    deltas.Add(new ModelTextDelta(text.Text));
                    break;
                case FunctionCallContent call:
                    _argumentBytes = ProviderProcessingGuard.Add(
                        _argumentBytes,
                        ProviderProcessingGuard.GetArgumentBytes(call),
                        _limits?.MaxProviderToolArgumentBytes);
                    break;
                case UsageContent:
                    usageContents++;
                    break;
                default:
                    throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                        "The chat client stream returned an unsupported content type.");
            }
        }
        if (usageContents > 0)
        {
            if (usageContents != update.Contents.Count)
                throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                    "The chat client stream mixed usage with response content.");
            if (usageContents != 1)
                throw MicrosoftExtensionsAIExceptionMapper.InvalidUsage();
            _usageUpdate = update.Clone();
        }
        else
        {
            _updates.Add(update.Clone());
        }
        return deltas;
    }

    public ModelResponse Complete()
    {
        if (_finishReason is null)
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client stream ended without a finish reason.");
        if (_usageUpdate is not null)
            _updates.Add(_usageUpdate);
        ChatResponse response;
        try { response = _updates.ToChatResponse(); }
        catch (Exception exception)
        {
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The chat client stream could not be reconstructed.", exception);
        }
        var mapped = MicrosoftExtensionsAIResponseMapper.Map(response);
        ProviderProcessingGuard.Validate(mapped, _limits);
        if (_text.Length > 0 && !StringComparer.Ordinal.Equals(_text.ToString(), mapped.Message.Text))
            throw MicrosoftExtensionsAIExceptionMapper.Protocol(
                "The reconstructed response contradicts streamed text.");
        return mapped;
    }
}
