using System.Text.Json;

namespace AgentForge.Abstractions;

/// <summary>Describes a tool exposed to a model.</summary>
public sealed record ToolDescriptor
{
    /// <summary>Creates an immutable tool descriptor with an owned schema.</summary>
    /// <param name="name">The unique ordinal tool name.</param>
    /// <param name="description">A description for the model.</param>
    /// <param name="parametersSchema">A JSON Schema describing accepted arguments.</param>
    public ToolDescriptor(string name, string description, JsonElement parametersSchema)
    {
        Name = name;
        Description = description;
        ParametersSchema = JsonValueSnapshot.CloneOrUndefined(parametersSchema);
    }

    /// <summary>Gets the unique ordinal tool name.</summary>
    public string Name { get; }

    /// <summary>Gets the model-facing description.</summary>
    public string Description { get; }

    /// <summary>Gets the owned JSON parameter schema.</summary>
    public JsonElement ParametersSchema { get; }
}

/// <summary>A model-requested tool invocation.</summary>
public sealed record ToolCall
{
    /// <summary>Creates an immutable tool call with owned arguments.</summary>
    /// <param name="id">The model-generated correlation identifier.</param>
    /// <param name="name">The requested tool name.</param>
    /// <param name="arguments">The JSON arguments.</param>
    public ToolCall(string id, string name, JsonElement arguments)
    {
        Id = id;
        Name = name;
        Arguments = JsonValueSnapshot.CloneOrUndefined(arguments);
    }

    /// <summary>Gets the model-generated call identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the requested tool name.</summary>
    public string Name { get; }

    /// <summary>Gets the owned JSON arguments.</summary>
    public JsonElement Arguments { get; }
}

/// <summary>Context supplied to a tool implementation.</summary>
public sealed record ToolInvocation
{
    /// <summary>Creates an immutable invocation with owned arguments.</summary>
    /// <param name="executionId">The current execution identifier.</param>
    /// <param name="callId">The model-generated call identifier.</param>
    /// <param name="arguments">The JSON arguments.</param>
    public ToolInvocation(ExecutionId executionId, string callId, JsonElement arguments)
    {
        ExecutionId = executionId;
        CallId = callId;
        Arguments = JsonValueSnapshot.CloneOrUndefined(arguments);
    }

    /// <summary>Gets the execution identifier.</summary>
    public ExecutionId ExecutionId { get; }

    /// <summary>Gets the tool-call identifier.</summary>
    public string CallId { get; }

    /// <summary>Gets the owned JSON arguments.</summary>
    public JsonElement Arguments { get; }
}

/// <summary>A successful tool result.</summary>
public sealed record ToolResult
{
    /// <summary>Creates an immutable result with an owned JSON value.</summary>
    /// <param name="value">The JSON result value.</param>
    public ToolResult(JsonElement value) => Value = JsonValueSnapshot.CloneOrUndefined(value);

    /// <summary>Gets the owned JSON result.</summary>
    public JsonElement Value { get; }
}

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
