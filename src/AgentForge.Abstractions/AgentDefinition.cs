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
    {
        ArgumentNullException.ThrowIfNull(allowedTools);
        Id = id;
        Instructions = instructions;
        Model = model;
        AllowedTools = allowedTools.ToFrozenSet(StringComparer.Ordinal);
        Limits = limits;
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
}

/// <summary>Defines hard upper bounds for one execution.</summary>
/// <param name="MaxModelCalls">Maximum number of provider invocations.</param>
/// <param name="MaxToolCalls">Maximum number of tool invocations.</param>
public sealed record AgentExecutionLimits(int MaxModelCalls = 8, int MaxToolCalls = 8);

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
