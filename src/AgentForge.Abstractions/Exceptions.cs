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
    public AgentExecutionException(AgentExecutionErrorCode errorCode, ExecutionId executionId, string message)
        : base(message)
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

    /// <summary>Gets the tool name.</summary>
    public string ToolName { get; }

    /// <summary>Gets the tool-call identifier.</summary>
    public string ToolCallId { get; }
}
