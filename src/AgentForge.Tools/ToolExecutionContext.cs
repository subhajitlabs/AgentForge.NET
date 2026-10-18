using AgentForge.Abstractions;

namespace AgentForge.Tools;

/// <summary>Identifies the execution and model tool call supplied to a typed tool handler.</summary>
/// <param name="ExecutionId">The current agent execution identifier.</param>
/// <param name="ToolCallId">The model-generated tool-call identifier.</param>
public sealed record ToolExecutionContext(ExecutionId ExecutionId, string ToolCallId);
