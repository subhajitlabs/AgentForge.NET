namespace AgentForge.Core;

/// <summary>Describes the bounded, content-free terminal state of one started agent execution.</summary>
public sealed record AgentExecutionDiagnostic
{
    internal AgentExecutionDiagnostic(
        string executionMode,
        string terminalOutcome,
        string? errorCode,
        string? providerFailureKind,
        int modelInvocationCount,
        int toolInvocationCount,
        long? observedInputTokens,
        long? observedOutputTokens,
        TimeSpan elapsedDuration)
    {
        ExecutionMode = executionMode;
        TerminalOutcome = terminalOutcome;
        ErrorCode = errorCode;
        ProviderFailureKind = providerFailureKind;
        ModelInvocationCount = modelInvocationCount;
        ToolInvocationCount = toolInvocationCount;
        ObservedInputTokens = observedInputTokens;
        ObservedOutputTokens = observedOutputTokens;
        ElapsedDuration = elapsedDuration;
    }

    /// <summary>Gets the execution mode: <c>buffered</c> or <c>streaming</c>.</summary>
    public string ExecutionMode { get; }

    /// <summary>Gets the stable terminal outcome.</summary>
    public string TerminalOutcome { get; }

    /// <summary>Gets the stable AgentForge error code, when applicable.</summary>
    public string? ErrorCode { get; }

    /// <summary>Gets the provider-neutral failure classification, when applicable.</summary>
    public string? ProviderFailureKind { get; }

    /// <summary>Gets the number of model invocations that actually started.</summary>
    public int ModelInvocationCount { get; }

    /// <summary>Gets the number of tool invocations that actually started.</summary>
    public int ToolInvocationCount { get; }

    /// <summary>Gets the accumulated authoritative input-token count, or <see langword="null"/> when unavailable.</summary>
    public long? ObservedInputTokens { get; }

    /// <summary>Gets the accumulated authoritative output-token count, or <see langword="null"/> when unavailable.</summary>
    public long? ObservedOutputTokens { get; }

    /// <summary>Gets the elapsed execution or streaming-enumeration duration.</summary>
    public TimeSpan ElapsedDuration { get; }
}
