namespace AgentForge.Abstractions;

/// <summary>Represents one configured agent bound to the shared AgentForge runtime.</summary>
public interface IAgent
{
    /// <summary>Gets the immutable definition used by this agent.</summary>
    AgentDefinition Definition { get; }

    /// <summary>Runs the agent with one user message.</summary>
    /// <param name="message">The non-empty user message.</param>
    /// <param name="cancellationToken">Cancels the execution.</param>
    /// <returns>The completed run.</returns>
    Task<AgentRunResult> RunAsync(
        string message,
        CancellationToken cancellationToken = default);

    /// <summary>Streams the agent with one user message.</summary>
    /// <param name="message">The non-empty user message.</param>
    /// <param name="cancellationToken">Cancels enumeration and execution.</param>
    /// <returns>The runtime event stream.</returns>
    IAsyncEnumerable<AgentRunEvent> StreamAsync(
        string message,
        CancellationToken cancellationToken = default);
}
