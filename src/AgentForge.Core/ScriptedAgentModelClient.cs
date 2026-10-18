using AgentForge.Abstractions;

namespace AgentForge.Core;

/// <summary>A deterministic model client that consumes scripted response functions in order.</summary>
public sealed class ScriptedAgentModelClient : IAgentModelClient
{
    private readonly object _gate = new();
    private readonly Queue<Func<ModelRequest, CancellationToken, Task<ModelResponse>>> _steps;

    /// <summary>Creates a client from asynchronous scripted steps.</summary>
    /// <param name="steps">Steps consumed once in FIFO order.</param>
    public ScriptedAgentModelClient(
        IEnumerable<Func<ModelRequest, CancellationToken, Task<ModelResponse>>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = new Queue<Func<ModelRequest, CancellationToken, Task<ModelResponse>>>(steps);
    }

    /// <summary>Creates a client from fixed responses.</summary>
    /// <param name="responses">Responses consumed once in FIFO order.</param>
    /// <returns>A scripted client.</returns>
    public static ScriptedAgentModelClient FromResponses(params ModelResponse[] responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        return new(responses.Select<ModelResponse, Func<ModelRequest, CancellationToken, Task<ModelResponse>>>(
            response => (_, _) => Task.FromResult(response)));
    }

    /// <inheritdoc />
    public Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Func<ModelRequest, CancellationToken, Task<ModelResponse>> step;
        lock (_gate)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("The scripted model client has no remaining responses.");
            }

            step = _steps.Dequeue();
        }

        return step(request, cancellationToken);
    }
}
