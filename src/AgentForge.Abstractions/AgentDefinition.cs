namespace AgentForge.Abstractions;

/// <summary>Defines immutable agent policy and model configuration.</summary>
/// <param name="Id">The agent identifier.</param>
/// <param name="Instructions">System instructions supplied to the model.</param>
/// <param name="Model">The selected model.</param>
/// <param name="AllowedTools">Tool names this agent may invoke.</param>
/// <param name="Limits">Hard execution limits.</param>
public sealed record AgentDefinition(
    AgentId Id,
    string Instructions,
    ModelId Model,
    IReadOnlySet<string> AllowedTools,
    AgentExecutionLimits Limits);

/// <summary>Defines hard upper bounds for one execution.</summary>
/// <param name="MaxModelCalls">Maximum number of provider invocations.</param>
/// <param name="MaxToolCalls">Maximum number of tool invocations.</param>
public sealed record AgentExecutionLimits(int MaxModelCalls = 8, int MaxToolCalls = 8);

/// <summary>Requests one isolated agent execution.</summary>
/// <param name="Agent">The agent to execute.</param>
/// <param name="Messages">Initial user messages.</param>
/// <param name="ConversationId">Optional caller-owned conversation correlation identifier.</param>
public sealed record AgentRunRequest(
    AgentDefinition Agent,
    IReadOnlyList<AgentMessage> Messages,
    string? ConversationId = null);

/// <summary>Describes a successfully completed execution.</summary>
/// <param name="ExecutionId">The generated execution identifier.</param>
/// <param name="FinalMessage">The terminal assistant message.</param>
/// <param name="Transcript">The immutable final transcript.</param>
/// <param name="Usage">Aggregated model usage when reported.</param>
/// <param name="ModelCalls">Number of model calls performed.</param>
/// <param name="ToolCalls">Number of tool calls performed.</param>
public sealed record AgentRunResult(
    ExecutionId ExecutionId,
    AssistantMessage FinalMessage,
    IReadOnlyList<AgentMessage> Transcript,
    ModelUsage? Usage,
    int ModelCalls,
    int ToolCalls);
