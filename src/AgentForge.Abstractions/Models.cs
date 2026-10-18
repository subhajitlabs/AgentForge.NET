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

/// <summary>Output from one model invocation with an explicit terminal reason.</summary>
public sealed record ModelResponse
{
    /// <summary>Creates a validated model response.</summary>
    /// <param name="message">Exactly one assistant message.</param>
    /// <param name="finishReason">The provider-neutral reason the model turn ended.</param>
    /// <param name="usage">Optional usage reported by the provider.</param>
    public ModelResponse(
        AssistantMessage message,
        ModelFinishReason finishReason,
        ModelUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Enum.IsDefined(finishReason))
            throw new ArgumentOutOfRangeException(nameof(finishReason));

        var hasText = !string.IsNullOrWhiteSpace(message.Text);
        var hasToolCalls = message.ToolCalls is { Count: > 0 };

        if (message.ToolCalls is null)
            throw new ArgumentException("The assistant tool-call collection cannot be null.", nameof(message));

        switch (finishReason)
        {
            case ModelFinishReason.Completed when !hasText || hasToolCalls:
                throw InvalidCombination(nameof(message));
            case ModelFinishReason.ToolCalls when hasText || !hasToolCalls:
                throw InvalidCombination(nameof(message));
            case ModelFinishReason.LengthLimit or ModelFinishReason.ContentFiltered or ModelFinishReason.Refused
                when hasToolCalls:
                throw InvalidCombination(nameof(message));
            case ModelFinishReason.Cancelled when hasText || hasToolCalls:
                throw InvalidCombination(nameof(message));
            case ModelFinishReason.Unknown when hasToolCalls:
                throw InvalidCombination(nameof(message));
        }

        Message = message;
        FinishReason = finishReason;
        Usage = usage;
    }

    /// <summary>Gets the assistant message.</summary>
    public AssistantMessage Message { get; }

    /// <summary>Gets the provider-neutral reason the model turn ended.</summary>
    public ModelFinishReason FinishReason { get; }

    /// <summary>Gets optional provider-reported usage.</summary>
    public ModelUsage? Usage { get; }

    /// <summary>Creates a completed terminal text response.</summary>
    /// <param name="message">A non-empty text-only assistant message.</param>
    /// <param name="usage">Optional usage reported by the provider.</param>
    /// <returns>A validated completed response.</returns>
    public static ModelResponse Completed(AssistantMessage message, ModelUsage? usage = null) =>
        new(message, ModelFinishReason.Completed, usage);

    /// <summary>Creates a response requesting one or more tools.</summary>
    /// <param name="message">A tool-call-only assistant message.</param>
    /// <param name="usage">Optional usage reported by the provider.</param>
    /// <returns>A validated tool-call response.</returns>
    public static ModelResponse ToolCalls(AssistantMessage message, ModelUsage? usage = null) =>
        new(message, ModelFinishReason.ToolCalls, usage);

    private static ArgumentException InvalidCombination(string parameterName) =>
        new("The assistant message is incompatible with the model finish reason.", parameterName);
}

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
