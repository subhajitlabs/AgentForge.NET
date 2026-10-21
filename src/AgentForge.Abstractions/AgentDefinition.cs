using System.Collections.Frozen;

namespace AgentForge.Abstractions;

/// <summary>Defines immutable agent policy and model configuration.</summary>
public sealed record AgentDefinition
{
    /// <summary>Creates an immutable agent definition.</summary>
    /// <param name="id">The agent identifier.</param>
    /// <param name="instructions">System instructions supplied to the model.</param>
    /// <param name="model">The selected model.</param>
    /// <param name="allowedTools">Tool names this agent may invoke.</param>
    /// <param name="limits">Hard execution limits.</param>
    public AgentDefinition(
        AgentId id,
        string instructions,
        ModelId model,
        IEnumerable<string> allowedTools,
        AgentExecutionLimits limits)
        : this(id, instructions, model, allowedTools, limits, AgentExecutionOptions.None)
    {
    }

    /// <summary>Creates an immutable agent definition with execution options.</summary>
    /// <param name="id">The agent identifier.</param>
    /// <param name="instructions">System instructions supplied to the model.</param>
    /// <param name="model">The selected model.</param>
    /// <param name="allowedTools">Tool names this agent may invoke.</param>
    /// <param name="limits">Hard execution limits.</param>
    /// <param name="options">Optional execution behavior.</param>
    public AgentDefinition(
        AgentId id,
        string instructions,
        ModelId model,
        IEnumerable<string> allowedTools,
        AgentExecutionLimits limits,
        AgentExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(allowedTools);
        ArgumentNullException.ThrowIfNull(options);
        Id = id;
        Instructions = instructions;
        Model = model;
        AllowedTools = allowedTools.ToFrozenSet(StringComparer.Ordinal);
        Limits = limits;
        Options = options;
    }

    /// <summary>Gets the agent identifier.</summary>
    public AgentId Id { get; }

    /// <summary>Gets the system instructions.</summary>
    public string Instructions { get; }

    /// <summary>Gets the selected model.</summary>
    public ModelId Model { get; }

    /// <summary>Gets the immutable tool allow-list.</summary>
    public IReadOnlySet<string> AllowedTools { get; }

    /// <summary>Gets the hard execution limits.</summary>
    public AgentExecutionLimits Limits { get; }

    /// <summary>Gets optional execution behavior.</summary>
    public AgentExecutionOptions Options { get; }
}

/// <summary>Defines hard upper bounds for one execution.</summary>
/// <param name="MaxModelCalls">Maximum number of provider invocations.</param>
/// <param name="MaxToolCalls">Maximum number of tool invocations.</param>
public sealed record AgentExecutionLimits(int MaxModelCalls = 8, int MaxToolCalls = 8);

/// <summary>Defines optional resource budgets for one execution.</summary>
public sealed record AgentResourceLimits
{
    /// <summary>Creates optional resource budgets. Every configured value must be strictly positive.</summary>
    /// <param name="maxInputCharacters">Maximum model-facing transcript text, measured in UTF-16 characters.</param>
    /// <param name="maxOutputCharacters">Maximum accumulated model output text, measured in UTF-16 characters.</param>
    /// <param name="maxToolArgumentBytes">Maximum accumulated tool argument JSON, measured in UTF-8 bytes.</param>
    /// <param name="maxToolResultBytes">Maximum accumulated tool result JSON, measured in UTF-8 bytes.</param>
    /// <param name="maxProviderStreamEvents">Maximum SDK streaming events processed per model turn.</param>
    /// <param name="maxProviderContentCharacters">Maximum provider text retained per model turn, measured in UTF-16 characters.</param>
    /// <param name="maxProviderToolArgumentBytes">Maximum provider tool-call arguments retained per model turn, measured in UTF-8 bytes.</param>
    public AgentResourceLimits(
        long? maxInputCharacters = null,
        long? maxOutputCharacters = null,
        long? maxToolArgumentBytes = null,
        long? maxToolResultBytes = null,
        long? maxProviderStreamEvents = null,
        long? maxProviderContentCharacters = null,
        long? maxProviderToolArgumentBytes = null)
    {
        ValidatePositive(maxInputCharacters, nameof(maxInputCharacters));
        ValidatePositive(maxOutputCharacters, nameof(maxOutputCharacters));
        ValidatePositive(maxToolArgumentBytes, nameof(maxToolArgumentBytes));
        ValidatePositive(maxToolResultBytes, nameof(maxToolResultBytes));
        ValidatePositive(maxProviderStreamEvents, nameof(maxProviderStreamEvents));
        ValidatePositive(maxProviderContentCharacters, nameof(maxProviderContentCharacters));
        ValidatePositive(maxProviderToolArgumentBytes, nameof(maxProviderToolArgumentBytes));
        MaxInputCharacters = maxInputCharacters;
        MaxOutputCharacters = maxOutputCharacters;
        MaxToolArgumentBytes = maxToolArgumentBytes;
        MaxToolResultBytes = maxToolResultBytes;
        MaxProviderStreamEvents = maxProviderStreamEvents;
        MaxProviderContentCharacters = maxProviderContentCharacters;
        MaxProviderToolArgumentBytes = maxProviderToolArgumentBytes;
    }

    /// <summary>Gets the maximum model-facing transcript text, or <see langword="null"/> when disabled.</summary>
    public long? MaxInputCharacters { get; }

    /// <summary>Gets the maximum accumulated model output text, or <see langword="null"/> when disabled.</summary>
    public long? MaxOutputCharacters { get; }

    /// <summary>Gets the maximum accumulated tool argument JSON bytes, or <see langword="null"/> when disabled.</summary>
    public long? MaxToolArgumentBytes { get; }

    /// <summary>Gets the maximum accumulated tool result JSON bytes, or <see langword="null"/> when disabled.</summary>
    public long? MaxToolResultBytes { get; }

    /// <summary>Gets the maximum SDK streaming events processed in one model turn, or <see langword="null"/> when disabled.</summary>
    public long? MaxProviderStreamEvents { get; }

    /// <summary>Gets the maximum provider text retained in one model turn, measured in UTF-16 characters, or <see langword="null"/> when disabled.</summary>
    public long? MaxProviderContentCharacters { get; }

    /// <summary>Gets the maximum provider tool-call arguments retained in one model turn, measured in UTF-8 bytes, or <see langword="null"/> when disabled.</summary>
    public long? MaxProviderToolArgumentBytes { get; }

    private static void ValidatePositive(long? value, string parameterName)
    {
        if (value is <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Configured resource limits must be positive.");
    }
}

/// <summary>Defines optional execution-wide budgets for provider-reported token usage.</summary>
public sealed record AgentTokenBudget
{
    /// <summary>Creates optional observed-token budgets. Every configured value must be strictly positive.</summary>
    public AgentTokenBudget(long? maxInputTokens = null, long? maxOutputTokens = null, long? maxTotalTokens = null)
    {
        ValidatePositive(maxInputTokens, nameof(maxInputTokens));
        ValidatePositive(maxOutputTokens, nameof(maxOutputTokens));
        ValidatePositive(maxTotalTokens, nameof(maxTotalTokens));
        MaxInputTokens = maxInputTokens;
        MaxOutputTokens = maxOutputTokens;
        MaxTotalTokens = maxTotalTokens;
    }

    /// <summary>Gets the maximum observed input tokens, or <see langword="null"/> when disabled.</summary>
    public long? MaxInputTokens { get; }
    /// <summary>Gets the maximum observed output tokens, or <see langword="null"/> when disabled.</summary>
    public long? MaxOutputTokens { get; }
    /// <summary>Gets the maximum observed total tokens, or <see langword="null"/> when disabled.</summary>
    public long? MaxTotalTokens { get; }
    /// <summary>Gets whether at least one observed-token budget is configured.</summary>
    public bool IsEnabled => MaxInputTokens is not null || MaxOutputTokens is not null || MaxTotalTokens is not null;

    private static void ValidatePositive(long? value, string parameterName)
    {
        if (value is <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Configured token budgets must be positive.");
    }
}

/// <summary>Defines optional behavior for one agent execution.</summary>
public sealed record AgentExecutionOptions
{
    /// <summary>Gets options that preserve the unbounded execution-duration behavior.</summary>
    public static AgentExecutionOptions None { get; } = new();

    /// <summary>Creates execution options with no runtime-owned timeout.</summary>
    public AgentExecutionOptions()
    {
    }

    /// <summary>Creates execution options with a whole-execution timeout.</summary>
    /// <param name="executionTimeout">The strictly positive maximum execution duration.</param>
    public AgentExecutionOptions(TimeSpan executionTimeout)
        : this(executionTimeout, null, null)
    {
    }

    /// <summary>Creates execution options with resource budgets and no runtime-owned timeout.</summary>
    /// <param name="resourceLimits">Optional resource budgets.</param>
    public AgentExecutionOptions(AgentResourceLimits resourceLimits)
        : this(null, resourceLimits, null)
    {
    }

    /// <summary>Creates execution options with observed-token budgets and no other optional policy.</summary>
    public AgentExecutionOptions(AgentTokenBudget tokenBudget)
        : this(null, null, tokenBudget)
    {
    }

    /// <summary>Creates execution options with an optional timeout and optional resource budgets.</summary>
    /// <param name="executionTimeout">The strictly positive maximum execution duration, or <see langword="null"/>.</param>
    /// <param name="resourceLimits">Optional resource budgets, or <see langword="null"/>.</param>
    public AgentExecutionOptions(TimeSpan? executionTimeout, AgentResourceLimits? resourceLimits)
        : this(executionTimeout, resourceLimits, null)
    {
    }

    /// <summary>Creates execution options with optional timeout, resource budgets, and observed-token budgets.</summary>
    public AgentExecutionOptions(TimeSpan? executionTimeout, AgentResourceLimits? resourceLimits, AgentTokenBudget? tokenBudget)
    {
        if (executionTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(executionTimeout), "The execution timeout must be positive.");
        ExecutionTimeout = executionTimeout;
        ResourceLimits = resourceLimits;
        TokenBudget = tokenBudget;
    }

    /// <summary>Gets the whole-execution timeout, or <see langword="null"/> when disabled.</summary>
    public TimeSpan? ExecutionTimeout { get; }

    /// <summary>Gets the resource budgets, or <see langword="null"/> when all are disabled.</summary>
    public AgentResourceLimits? ResourceLimits { get; }

    /// <summary>Gets execution-wide provider-reported token budgets, or <see langword="null"/> when disabled.</summary>
    public AgentTokenBudget? TokenBudget { get; }
}

/// <summary>Requests one isolated agent execution.</summary>
public sealed record AgentRunRequest
{
    /// <summary>Creates an immutable execution request.</summary>
    /// <param name="agent">The agent to execute.</param>
    /// <param name="messages">Initial user messages.</param>
    public AgentRunRequest(AgentDefinition agent, IEnumerable<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Agent = agent;
        Messages = Array.AsReadOnly(messages.ToArray());
    }

    /// <summary>Gets the agent to execute.</summary>
    public AgentDefinition Agent { get; }

    /// <summary>Gets the immutable initial message snapshot.</summary>
    public IReadOnlyList<AgentMessage> Messages { get; }
}

/// <summary>Describes a successfully completed execution.</summary>
public sealed record AgentRunResult
{
    /// <summary>Creates an immutable completed-run snapshot.</summary>
    /// <param name="executionId">The generated execution identifier.</param>
    /// <param name="finalMessage">The terminal assistant message.</param>
    /// <param name="transcript">The final transcript.</param>
    /// <param name="usage">Aggregated model usage when reported.</param>
    /// <param name="modelCalls">Number of model calls performed.</param>
    /// <param name="toolCalls">Number of tool calls performed.</param>
    public AgentRunResult(
        ExecutionId executionId,
        AssistantMessage finalMessage,
        IEnumerable<AgentMessage> transcript,
        ModelUsage? usage,
        int modelCalls,
        int toolCalls)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(finalMessage);
        ExecutionId = executionId;
        FinalMessage = finalMessage;
        Transcript = Array.AsReadOnly(transcript.ToArray());
        Usage = usage;
        ModelCalls = modelCalls;
        ToolCalls = toolCalls;
    }

    /// <summary>Gets the generated execution identifier.</summary>
    public ExecutionId ExecutionId { get; }

    /// <summary>Gets the terminal assistant message.</summary>
    public AssistantMessage FinalMessage { get; }

    /// <summary>Gets the immutable final transcript snapshot.</summary>
    public IReadOnlyList<AgentMessage> Transcript { get; }

    /// <summary>Gets aggregate provider usage when reported.</summary>
    public ModelUsage? Usage { get; }

    /// <summary>Gets the number of completed model calls.</summary>
    public int ModelCalls { get; }

    /// <summary>Gets the number of completed tool calls.</summary>
    public int ToolCalls { get; }
}
