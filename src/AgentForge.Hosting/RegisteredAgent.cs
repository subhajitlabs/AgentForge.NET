using System.Runtime.CompilerServices;
using AgentForge.Abstractions;

namespace AgentForge.Hosting;

internal sealed class RegisteredAgent(
    AgentDefinition definition,
    IAgentRuntime runtime,
    IStreamingAgentRuntime streamingRuntime) : IAgent
{
    public AgentDefinition Definition { get; } = definition;

    public Task<AgentRunResult> RunAsync(string message, CancellationToken cancellationToken = default) =>
        runtime.RunAsync(CreateRequest(message), cancellationToken);

    public async IAsyncEnumerable<AgentRunEvent> StreamAsync(
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var runEvent in streamingRuntime.StreamAsync(CreateRequest(message), cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return runEvent;
        }
    }

    private AgentRunRequest CreateRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("The user message cannot be empty.", nameof(message));
        return new AgentRunRequest(Definition, [new UserMessage(message)]);
    }
}
