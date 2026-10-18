using System.Text.Json;

namespace AgentForge.Abstractions;

/// <summary>Base type for provider-neutral conversation messages.</summary>
public abstract record AgentMessage
{
    private protected AgentMessage() { }
}

/// <summary>A system instruction message.</summary>
/// <param name="Text">The instruction text.</param>
public sealed record SystemMessage(string Text) : AgentMessage;

/// <summary>A user-authored text message.</summary>
/// <param name="Text">The user text.</param>
public sealed record UserMessage(string Text) : AgentMessage;

/// <summary>An assistant message containing either text or ordered tool calls.</summary>
public sealed record AssistantMessage : AgentMessage
{
    /// <summary>Creates an immutable assistant message.</summary>
    /// <param name="text">Terminal assistant text, or <see langword="null"/> for tool calls.</param>
    /// <param name="toolCalls">Ordered tool calls, empty for terminal text.</param>
    public AssistantMessage(string? text, IEnumerable<ToolCall> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        Text = text;
        ToolCalls = Array.AsReadOnly(toolCalls.ToArray());
    }

    /// <summary>Gets terminal assistant text, or <see langword="null"/> for tool calls.</summary>
    public string? Text { get; }

    /// <summary>Gets the immutable ordered tool-call snapshot.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; }

    /// <summary>Creates a terminal text response.</summary>
    /// <param name="text">Non-empty response text.</param>
    public static AssistantMessage FromText(string text) => new(text, Array.Empty<ToolCall>());

    /// <summary>Creates an assistant tool-call message.</summary>
    /// <param name="toolCalls">Ordered requested tool calls.</param>
    public static AssistantMessage FromToolCalls(params ToolCall[] toolCalls) => new(null, toolCalls);
}

/// <summary>A tool result correlated to an assistant tool call.</summary>
public sealed record ToolResultMessage : AgentMessage
{
    /// <summary>Creates an immutable correlated tool-result message.</summary>
    /// <param name="toolCallId">The originating tool-call identifier.</param>
    /// <param name="toolName">The invoked tool name.</param>
    /// <param name="result">The JSON result.</param>
    public ToolResultMessage(string toolCallId, string toolName, JsonElement result)
    {
        ToolCallId = toolCallId;
        ToolName = toolName;
        Result = JsonValueSnapshot.CloneOrUndefined(result);
    }

    /// <summary>Gets the originating tool-call identifier.</summary>
    public string ToolCallId { get; }

    /// <summary>Gets the invoked tool name.</summary>
    public string ToolName { get; }

    /// <summary>Gets the owned JSON result.</summary>
    public JsonElement Result { get; }
}
