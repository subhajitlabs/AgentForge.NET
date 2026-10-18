using Microsoft.Extensions.AI;

namespace AgentForge.Adapters.MicrosoftExtensionsAI.Tests;

internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _complete;
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _stream;

    public ScriptedChatClient(
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? complete = null,
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? stream = null)
    {
        _complete = complete ?? ((_, _, _) => throw new NotSupportedException());
        _stream = stream ?? ((_, _, _) => throw new NotSupportedException());
    }

    public int CompleteCalls { get; private set; }
    public int StreamCalls { get; private set; }
    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CompleteCalls++;
        Capture(messages, options);
        return _complete(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        StreamCalls++;
        Capture(messages, options);
        return _stream(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private void Capture(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        LastMessages = messages.Select(message => message.Clone()).ToArray();
        LastOptions = options?.Clone();
    }
}
