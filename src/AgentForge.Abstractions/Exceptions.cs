namespace AgentForge.Abstractions;

/// <summary>Identifies a stable runtime policy or contract failure.</summary>
public enum AgentExecutionErrorCode
{
    /// <summary>The model-call limit would be exceeded.</summary>
    ModelCallLimitExceeded,
    /// <summary>The tool-call limit would be exceeded.</summary>
    ToolCallLimitExceeded,
    /// <summary>The model requested a tool not allowed by the agent.</summary>
    ToolNotAllowed,
    /// <summary>The model requested an unregistered tool.</summary>
    ToolNotFound,
    /// <summary>The model returned a response that violates the contract.</summary>
    InvalidModelResponse
}

/// <summary>A runtime policy or model-contract violation.</summary>
public sealed class AgentExecutionException : Exception
{
    /// <summary>Creates an execution exception.</summary>
    /// <param name="errorCode">The stable failure code.</param>
    /// <param name="executionId">The execution identifier.</param>
    /// <param name="message">A safe diagnostic message.</param>
    /// <param name="innerException">The underlying cause, when applicable.</param>
    public AgentExecutionException(
        AgentExecutionErrorCode errorCode,
        ExecutionId executionId,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        ExecutionId = executionId;
    }

    /// <summary>Gets the stable failure code.</summary>
    public AgentExecutionErrorCode ErrorCode { get; }

    /// <summary>Gets the execution identifier.</summary>
    public ExecutionId ExecutionId { get; }
}

/// <summary>A provider failure enriched with execution context.</summary>
public sealed class AgentProviderException : Exception
{
    /// <summary>Creates a provider exception.</summary>
    public AgentProviderException(ExecutionId executionId, string message, Exception innerException)
        : base(message, innerException) => ExecutionId = executionId;

    /// <summary>Gets the execution identifier.</summary>
    public ExecutionId ExecutionId { get; }
}

/// <summary>A tool failure enriched with invocation context.</summary>
public sealed class AgentToolException : Exception
{
    /// <summary>Creates a tool exception.</summary>
    public AgentToolException(
        ExecutionId executionId,
        string toolName,
        string toolCallId,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        ExecutionId = executionId;
        ToolName = toolName;
        ToolCallId = toolCallId;
    }

    /// <summary>Gets the execution identifier.</summary>
    public ExecutionId ExecutionId { get; }

    /// <summary>
    /// Gets the untrusted model-supplied tool name for structured diagnostics.
    /// Do not log this value without an explicit redaction policy.
    /// </summary>
    public string ToolName { get; }

    /// <summary>
    /// Gets the untrusted model-supplied call identifier for structured diagnostics.
    /// Do not log this value without an explicit redaction policy.
    /// </summary>
    public string ToolCallId { get; }
}
