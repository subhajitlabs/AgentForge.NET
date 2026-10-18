namespace AgentForge.Abstractions;

/// <summary>Input for one model invocation.</summary>
/// <param name="ExecutionId">The current execution identifier.</param>
/// <param name="Model">The selected model.</param>
/// <param name="Messages">The complete transcript snapshot.</param>
/// <param name="AvailableTools">Tools permitted for this agent.</param>
public sealed record ModelRequest(
    ExecutionId ExecutionId,
    ModelId Model,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolDescriptor> AvailableTools);

/// <summary>Output from one model invocation.</summary>
/// <param name="Message">Exactly one assistant message.</param>
/// <param name="Usage">Optional usage reported by the provider.</param>
public sealed record ModelResponse(AssistantMessage Message, ModelUsage? Usage = null);

/// <summary>Provider-reported token usage.</summary>
/// <param name="InputTokens">Input token count.</param>
/// <param name="OutputTokens">Output token count.</param>
public sealed record ModelUsage(long InputTokens, long OutputTokens)
{
    /// <summary>Total input and output tokens.</summary>
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>Completes one provider-neutral model turn.</summary>
public interface IAgentModelClient
{
    /// <summary>Requests the next assistant message.</summary>
    /// <param name="request">The model request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The model response.</returns>
    Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
