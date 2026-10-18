namespace AgentForge.Abstractions;

/// <summary>Streams one agent run to authoritative completion.</summary>
public interface IStreamingAgentRuntime
{
    /// <summary>Executes an isolated run while exposing provisional assistant text.</summary>
    /// <param name="request">The execution request.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The runtime event stream.</returns>
    IAsyncEnumerable<AgentRunEvent> StreamAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Base type for agent-run stream events.</summary>
public abstract record AgentRunEvent;

/// <summary>A provisional assistant text fragment.</summary>
/// <param name="Text">The text fragment.</param>
public sealed record AssistantTextDelta(string Text) : AgentRunEvent;

/// <summary>The authoritative successful result of a streamed run.</summary>
/// <param name="Result">The completed run result.</param>
public sealed record AgentRunCompleted(AgentRunResult Result) : AgentRunEvent;
