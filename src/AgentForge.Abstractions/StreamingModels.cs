namespace AgentForge.Abstractions;

/// <summary>Streams one provider-neutral model turn.</summary>
public interface IStreamingAgentModelClient
{
    /// <summary>Streams provisional text and one authoritative terminal response.</summary>
    /// <param name="request">The model request.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The provider-neutral model event stream.</returns>
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Base type for provider-neutral model stream events.</summary>
public abstract record ModelStreamEvent;

/// <summary>A provisional assistant text fragment.</summary>
/// <param name="Text">The text fragment.</param>
public sealed record ModelTextDelta(string Text) : ModelStreamEvent;

/// <summary>The authoritative reconstructed response for one model turn.</summary>
/// <param name="Response">The complete model response.</param>
public sealed record ModelResponseCompleted(ModelResponse Response) : ModelStreamEvent;
