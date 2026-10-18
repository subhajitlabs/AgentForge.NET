using System.Text.Json;

namespace AgentForge.Abstractions;

/// <summary>Base type for provider-neutral conversation messages.</summary>
public abstract record AgentMessage;

/// <summary>A system instruction message.</summary>
/// <param name="Text">The instruction text.</param>
public sealed record SystemMessage(string Text) : AgentMessage;

/// <summary>A user-authored text message.</summary>
/// <param name="Text">The user text.</param>
public sealed record UserMessage(string Text) : AgentMessage;

/// <summary>An assistant message containing either text or ordered tool calls.</summary>
/// <param name="Text">Terminal assistant text, or <see langword="null"/> for tool calls.</param>
/// <param name="ToolCalls">Ordered tool calls, empty for terminal text.</param>
public sealed record AssistantMessage(string? Text, IReadOnlyList<ToolCall> ToolCalls) : AgentMessage
{
    /// <summary>Creates a terminal text response.</summary>
    /// <param name="text">Non-empty response text.</param>
    public static AssistantMessage FromText(string text) => new(text, Array.Empty<ToolCall>());

    /// <summary>Creates an assistant tool-call message.</summary>
    /// <param name="toolCalls">Ordered requested tool calls.</param>
    public static AssistantMessage FromToolCalls(params ToolCall[] toolCalls) => new(null, toolCalls);
}

/// <summary>A tool result correlated to an assistant tool call.</summary>
/// <param name="ToolCallId">The originating tool-call identifier.</param>
/// <param name="ToolName">The invoked tool name.</param>
/// <param name="Result">The JSON result.</param>
public sealed record ToolResultMessage(string ToolCallId, string ToolName, JsonElement Result) : AgentMessage;
