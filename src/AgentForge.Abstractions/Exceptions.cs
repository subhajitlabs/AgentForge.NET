namespace AgentForge.Abstractions;

/// <summary>Classifies a failure reported by a model-provider adapter.</summary>
public enum ModelProviderErrorKind
{
    /// <summary>Provider authentication failed.</summary>
    Authentication,
    /// <summary>The credential is authenticated but not authorized.</summary>
    Authorization,
    /// <summary>The provider rejected the request.</summary>
    InvalidRequest,
    /// <summary>The selected model was not found.</summary>
    ModelNotFound,
    /// <summary>The provider rate limit was reached.</summary>
    RateLimited,
    /// <summary>The operation timed out independently of caller cancellation.</summary>
    Timeout,
    /// <summary>The provider service was unavailable.</summary>
    ServiceUnavailable,
    /// <summary>The transport failed.</summary>
    Transport,
    /// <summary>The provider returned malformed or contradictory protocol data.</summary>
    Protocol,
    /// <summary>The failure could not be classified safely.</summary>
    Unknown
}

/// <summary>A sanitized failure raised by a model-provider adapter.</summary>
public sealed class ModelProviderException : Exception
{
    /// <summary>Creates a provider-adapter exception.</summary>
    public ModelProviderException(ModelProviderErrorKind kind, bool isTransient, string message,
        Exception? innerException = null, TimeSpan? retryAfter = null, string? providerRequestId = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
        ProviderRequestId = providerRequestId;
    }

    /// <summary>Gets the stable failure classification.</summary>
    public ModelProviderErrorKind Kind { get; }
    /// <summary>Gets whether retrying may succeed. AgentForge does not retry automatically.</summary>
    public bool IsTransient { get; }
    /// <summary>Gets the provider retry delay, when safely available.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>Gets a sanitized provider request identifier, when available.</summary>
    public string? ProviderRequestId { get; }
}

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
    /// <summary>The model supplied arguments that do not satisfy the selected tool's contract.</summary>
    InvalidToolArguments,
    /// <summary>The model returned a response that violates the contract.</summary>
    InvalidModelResponse,
    /// <summary>The model output ended before it was complete.</summary>
    ModelOutputIncomplete,
    /// <summary>The model output was blocked by content filtering.</summary>
    ModelOutputFiltered,
    /// <summary>The model refused the request.</summary>
    ModelRefused,
    /// <summary>The configured model client does not support the requested capability.</summary>
    CapabilityNotSupported
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
