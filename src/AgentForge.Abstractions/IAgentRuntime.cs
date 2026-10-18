namespace AgentForge.Abstractions;

/// <summary>Runs one agent to a terminal assistant response.</summary>
public interface IAgentRuntime
{
    /// <summary>Executes an isolated run.</summary>
    /// <param name="request">The execution request.</param>
    /// <param name="cancellationToken">Cancels the execution.</param>
    /// <returns>The completed run.</returns>
    Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);
}
