using AgentForge.Abstractions;
using AgentForge.Adapters.MicrosoftExtensionsAI;
using AgentForge.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

// Replace this deterministic client with a raw compatible IChatClient. Clients or middleware
// that automatically invoke tools are unsupported because AgentForge must own authorization.
using IChatClient chatClient = new OfflineChatClient();
await using var provider = new ServiceCollection()
    .AddMicrosoftExtensionsAIModelClient(chatClient)
    .AddAgentForge()
    .AddAgentDefinition("interop", agent => agent
        .WithInstructions("Answer concisely.")
        .UseModel("client-selected-model")
        .WithLimits(2, 1))
    .BuildServiceProvider();

var result = await provider.GetRequiredKeyedService<IAgent>("interop").RunAsync("Hello");
Console.WriteLine(result.FinalMessage.Text);

internal sealed class OfflineChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello from IChatClient."))
        {
            FinishReason = ChatFinishReason.Stop
        });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
