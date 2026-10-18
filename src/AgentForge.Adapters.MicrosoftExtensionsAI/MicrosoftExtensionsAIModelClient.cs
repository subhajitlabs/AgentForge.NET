using System.Runtime.CompilerServices;
using AgentForge.Abstractions;
using AgentForge.Adapters.MicrosoftExtensionsAI.Internal;
using Microsoft.Extensions.AI;

namespace AgentForge.Adapters.MicrosoftExtensionsAI;

/// <summary>Adapts an existing Microsoft.Extensions.AI chat client to AgentForge model contracts.</summary>
public sealed class MicrosoftExtensionsAIModelClient : IAgentModelClient, IStreamingAgentModelClient
{
    private readonly IChatClient _client;

    /// <summary>
    /// Creates an adapter over a caller-owned chat client. The adapter does not dispose the client and requires
    /// a raw client pipeline that does not invoke tools automatically.
    /// </summary>
    /// <param name="client">The caller-owned chat client.</param>
    public MicrosoftExtensionsAIModelClient(IChatClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public async Task<ModelResponse> CompleteAsync(
        ModelRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mapped = MicrosoftExtensionsAIRequestMapper.Map(request);
        try
        {
            var response = await _client.GetResponseAsync(
                mapped.Messages, mapped.Options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = MicrosoftExtensionsAIResponseMapper.Map(response);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NotSupportedException exception)
        {
            throw CapabilityError(request.ExecutionId, exception);
        }
        catch (Exception exception) { throw MicrosoftExtensionsAIExceptionMapper.Map(exception); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mapped = MicrosoftExtensionsAIRequestMapper.Map(request);
        var updates = GetUpdates(mapped.Messages, mapped.Options, request.ExecutionId, cancellationToken);
        var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        var accumulator = new MicrosoftExtensionsAIStreamAccumulator();
        try
        {
            while (await MoveNextAsync(enumerator, request.ExecutionId, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var delta in accumulator.Accept(enumerator.Current)) yield return delta;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var response = accumulator.Complete();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelResponseCompleted(response);
        }
        finally
        {
            await DisposeAsync(enumerator, request.ExecutionId, cancellationToken).ConfigureAwait(false);
        }
    }

    private IAsyncEnumerable<ChatResponseUpdate> GetUpdates(
        IReadOnlyList<ChatMessage> messages, ChatOptions options, ExecutionId executionId,
        CancellationToken cancellationToken)
    {
        try { return _client.GetStreamingResponseAsync(messages, options, cancellationToken); }
        catch (NotSupportedException exception) { throw CapabilityError(executionId, exception); }
        catch (Exception exception) { throw MicrosoftExtensionsAIExceptionMapper.Map(exception); }
    }

    private static async ValueTask<bool> MoveNextAsync(
        IAsyncEnumerator<ChatResponseUpdate> enumerator, ExecutionId executionId,
        CancellationToken cancellationToken)
    {
        try { return await enumerator.MoveNextAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NotSupportedException exception) { throw CapabilityError(executionId, exception); }
        catch (Exception exception) { throw MicrosoftExtensionsAIExceptionMapper.Map(exception); }
    }

    private static async ValueTask DisposeAsync(
        IAsyncEnumerator<ChatResponseUpdate> enumerator, ExecutionId executionId,
        CancellationToken cancellationToken)
    {
        try { await enumerator.DisposeAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NotSupportedException exception) { throw CapabilityError(executionId, exception); }
        catch (Exception exception) { throw MicrosoftExtensionsAIExceptionMapper.Map(exception); }
    }

    private static AgentExecutionException CapabilityError(ExecutionId executionId, Exception exception) =>
        new(AgentExecutionErrorCode.CapabilityNotSupported, executionId,
            "The configured chat client does not support the requested operation.", exception);
}
