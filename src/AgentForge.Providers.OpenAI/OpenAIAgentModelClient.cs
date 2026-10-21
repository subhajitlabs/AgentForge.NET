using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using AgentForge.Abstractions;
using AgentForge.Providers.OpenAI.Internal;
using OpenAI;
using OpenAI.Responses;

namespace AgentForge.Providers.OpenAI;

/// <summary>Adapts the OpenAI Responses API to AgentForge model-client contracts.</summary>
public sealed class OpenAIAgentModelClient : IAgentModelClient, IStreamingAgentModelClient
{
    private readonly ResponsesClient _client;

    /// <summary>Creates a provider-owned SDK client from validated options.</summary>
    public OpenAIAgentModelClient(OpenAIProviderOptions options)
        : this(CreateClient(OpenAIProviderOptionsSnapshot.Create(options))) { }

    /// <summary>
    /// Creates an adapter over a caller-owned SDK client. The caller is responsible for endpoint security
    /// and SDK retry behavior. AgentForge does not add another retry layer.
    /// </summary>
    public OpenAIAgentModelClient(ResponsesClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await _client.CreateResponseAsync(
                OpenAIRequestMapper.Map(request, streaming: false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var mapped = OpenAIResponseMapper.Map(result.Value);
            ProviderProcessingGuard.Validate(mapped, request.ResourceLimits);
            cancellationToken.ThrowIfCancellationRequested();
            return mapped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw OpenAIExceptionMapper.FromException(exception); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accumulator = new OpenAIStreamAccumulator(request.ResourceLimits);
        var eventCount = 0L;
        IAsyncEnumerator<StreamingResponseUpdate>? enumerator = null;
        try
        {
            var updates = CreateStreamingUpdates(
                OpenAIRequestMapper.Map(request, streaming: true), cancellationToken);
            enumerator = updates.GetAsyncEnumerator(cancellationToken);
            while (await MoveNextAsync(enumerator, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                eventCount = ProviderProcessingGuard.Add(
                    eventCount, 1, request.ResourceLimits?.MaxProviderStreamEvents);
                var delta = accumulator.Accept(enumerator.Current);
                if (delta is not null) yield return delta;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var response = accumulator.Complete();
            ProviderProcessingGuard.Validate(response, request.ResourceLimits);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelResponseCompleted(response);
        }
        finally
        {
            if (enumerator is not null)
                await DisposeAsync(enumerator, cancellationToken).ConfigureAwait(false);
        }
    }

    private AsyncCollectionResult<StreamingResponseUpdate> CreateStreamingUpdates(
        CreateResponseOptions options, CancellationToken cancellationToken)
    {
        try { return _client.CreateResponseStreamingAsync(options, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw OpenAIExceptionMapper.FromException(exception); }
    }

    private static async ValueTask<bool> MoveNextAsync(
        IAsyncEnumerator<StreamingResponseUpdate> enumerator, CancellationToken cancellationToken)
    {
        try { return await enumerator.MoveNextAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw OpenAIExceptionMapper.FromException(exception); }
    }

    private static async ValueTask DisposeAsync(
        IAsyncEnumerator<StreamingResponseUpdate> enumerator, CancellationToken cancellationToken)
    {
        try { await enumerator.DisposeAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw OpenAIExceptionMapper.FromException(exception); }
    }

    private static ResponsesClient CreateClient(OpenAIProviderOptionsSnapshot snapshot)
    {
        var options = new ResponsesClientOptions { RetryPolicy = new ClientRetryPolicy(0) };
        if (snapshot.Endpoint is not null) options.Endpoint = snapshot.Endpoint;
        return new ResponsesClient(new ApiKeyCredential(snapshot.ApiKey), options);
    }
}
