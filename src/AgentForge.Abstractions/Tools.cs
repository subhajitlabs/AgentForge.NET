using System.Text.Json;

namespace AgentForge.Abstractions;

/// <summary>Describes a tool exposed to a model.</summary>
/// <param name="Name">The unique ordinal tool name.</param>
/// <param name="Description">A description for the model.</param>
/// <param name="ParametersSchema">A JSON Schema describing accepted arguments.</param>
public sealed record ToolDescriptor(string Name, string Description, JsonElement ParametersSchema);

/// <summary>A model-requested tool invocation.</summary>
/// <param name="Id">The model-generated correlation identifier.</param>
/// <param name="Name">The requested tool name.</param>
/// <param name="Arguments">The JSON arguments.</param>
public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>Context supplied to a tool implementation.</summary>
/// <param name="ExecutionId">The current execution identifier.</param>
/// <param name="CallId">The model-generated call identifier.</param>
/// <param name="Arguments">The JSON arguments.</param>
public sealed record ToolInvocation(ExecutionId ExecutionId, string CallId, JsonElement Arguments);

/// <summary>A successful tool result.</summary>
/// <param name="Value">The JSON result value.</param>
public sealed record ToolResult(JsonElement Value);

/// <summary>Executes one trusted application tool.</summary>
public interface IAgentTool
{
    /// <summary>Gets metadata exposed to the model.</summary>
    ToolDescriptor Descriptor { get; }

    /// <summary>Invokes the tool.</summary>
    /// <param name="invocation">Validated invocation context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The JSON tool result.</returns>
    Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves registered tools by exact name.</summary>
public interface IAgentToolRegistry
{
    /// <summary>Returns descriptors for allowed registered tools.</summary>
    /// <param name="allowedTools">The agent allow-list.</param>
    /// <returns>Tool descriptors in deterministic name order.</returns>
    IReadOnlyList<ToolDescriptor> Describe(IReadOnlySet<string> allowedTools);

    /// <summary>Attempts to resolve an exact tool name.</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="tool">The resolved tool when found.</param>
    /// <returns><see langword="true"/> when registered.</returns>
    bool TryResolve(string name, out IAgentTool? tool);
}
