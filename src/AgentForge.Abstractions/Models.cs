namespace AgentForge.Abstractions;

/// <summary>Input for one model invocation.</summary>
public sealed record ModelRequest
{
    /// <summary>Creates an immutable model-request snapshot.</summary>
    /// <param name="executionId">The current execution identifier.</param>
    /// <param name="model">The selected model.</param>
    /// <param name="messages">The complete transcript.</param>
    /// <param name="availableTools">Tools permitted for this agent.</param>
    public ModelRequest(
        ExecutionId executionId,
        ModelId model,
        IEnumerable<AgentMessage> messages,
        IEnumerable<ToolDescriptor> availableTools)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(availableTools);
        ExecutionId = executionId;
        Model = model;
        Messages = Array.AsReadOnly(messages.ToArray());
        AvailableTools = Array.AsReadOnly(availableTools.ToArray());
    }

    /// <summary>Gets the current execution identifier.</summary>
    public ExecutionId ExecutionId { get; }

    /// <summary>Gets the selected model.</summary>
    public ModelId Model { get; }

    /// <summary>Gets the immutable transcript snapshot.</summary>
    public IReadOnlyList<AgentMessage> Messages { get; }

    /// <summary>Gets the immutable available-tool snapshot.</summary>
    public IReadOnlyList<ToolDescriptor> AvailableTools { get; }
}

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
    public long TotalTokens => checked(InputTokens + OutputTokens);
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
