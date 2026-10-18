using AgentForge.Abstractions;
using System.Runtime.CompilerServices;

namespace AgentForge.Core;

/// <summary>A deterministic streaming model client that consumes scripted turns in order.</summary>
public sealed class ScriptedStreamingAgentModelClient : IAgentModelClient, IStreamingAgentModelClient
{
    private readonly object _gate = new();
    private readonly Queue<Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>>> _steps;

    /// <summary>Creates a client from asynchronous scripted stream factories.</summary>
    /// <param name="steps">Steps consumed once in FIFO order.</param>
    public ScriptedStreamingAgentModelClient(
        IEnumerable<Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = new Queue<Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>>>(steps);
    }

    /// <summary>Creates a client from fixed event sequences.</summary>
    /// <param name="turns">Event sequences consumed once in FIFO order.</param>
    /// <returns>A scripted streaming client.</returns>
    public static ScriptedStreamingAgentModelClient FromEvents(params ModelStreamEvent[][] turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        return new(turns.Select<ModelStreamEvent[], Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>>>(
            events => (_, token) => EnumerateAsync(events, token)));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>> step;
        lock (_gate)
        {
            if (_steps.Count == 0)
                throw new InvalidOperationException("The scripted streaming model client has no remaining responses.");
            step = _steps.Dequeue();
        }

        return step(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ModelResponse? response = null;
        await foreach (var streamEvent in StreamAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (streamEvent is ModelResponseCompleted completed)
                response = completed.Response;
        }

        return response ?? throw new InvalidOperationException("The scripted stream ended without a completed response.");
    }

    private static async IAsyncEnumerable<ModelStreamEvent> EnumerateAsync(
        IEnumerable<ModelStreamEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var streamEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
            await Task.Yield();
        }
    }
}
