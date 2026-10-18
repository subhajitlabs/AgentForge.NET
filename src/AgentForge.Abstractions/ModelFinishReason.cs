namespace AgentForge.Abstractions;

/// <summary>Describes why a provider ended one model turn.</summary>
public enum ModelFinishReason
{
    /// <summary>The model produced a complete terminal text response.</summary>
    Completed,

    /// <summary>The model produced one or more tool calls.</summary>
    ToolCalls,

    /// <summary>The model stopped because an output-length limit was reached.</summary>
    LengthLimit,

    /// <summary>The model output was blocked or truncated by content filtering.</summary>
    ContentFiltered,

    /// <summary>The model refused the request.</summary>
    Refused,

    /// <summary>The provider reports that generation was cancelled.</summary>
    Cancelled,

    /// <summary>The provider returned a terminal reason that cannot be mapped safely.</summary>
    Unknown
}
