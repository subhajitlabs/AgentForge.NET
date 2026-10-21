using AgentForge.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AgentForge.Core;

/// <summary>Default deterministic single-agent runtime.</summary>
public sealed class AgentRuntime : IAgentRuntime, IStreamingAgentRuntime
{
    private readonly IAgentModelClient _modelClient;
    private readonly AgentExecutionEngine _executionEngine;

    /// <summary>Creates the runtime.</summary>
    /// <param name="modelClient">The provider-neutral model client.</param>
    /// <param name="toolRegistry">The trusted tool registry.</param>
    /// <param name="logger">An optional metadata-only logger.</param>
    public AgentRuntime(
        IAgentModelClient modelClient,
        IAgentToolRegistry toolRegistry,
        ILogger<AgentRuntime>? logger = null)
        : this(modelClient, toolRegistry, TimeProvider.System, logger)
    {
    }

    /// <summary>Creates the runtime with a controllable time source.</summary>
    /// <param name="modelClient">The provider-neutral model client.</param>
    /// <param name="toolRegistry">The trusted tool registry.</param>
    /// <param name="timeProvider">The time source used for execution deadlines.</param>
    /// <param name="logger">An optional metadata-only logger.</param>
    public AgentRuntime(
        IAgentModelClient modelClient,
        IAgentToolRegistry toolRegistry,
        TimeProvider timeProvider,
        ILogger<AgentRuntime>? logger = null)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _executionEngine = new AgentExecutionEngine(toolRegistry, logger ?? NullLogger<AgentRuntime>.Instance, timeProvider);
    }

    /// <inheritdoc />
    public Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default) =>
        _executionEngine.ExecuteAsync(
            request,
            (modelRequest, token) => _modelClient.CompleteAsync(modelRequest, token),
            "buffered",
            cancellationToken,
            callerCancellationToken: cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentRunEvent> StreamAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = Channel.CreateBounded<StreamEnvelope>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var execution = ProduceStreamingEventsAsync(
            request,
            events.Writer,
            executionCancellation.Token,
            cancellationToken);

        try
        {
            StreamEnvelope? pending = null;
            while (await events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!events.Reader.TryRead(out pending))
                    continue;
                if (pending.Event is AgentRunCompleted && pending.ExecutionToken.IsCancellationRequested)
                {
                    pending.Acknowledge();
                    pending = null;
                    continue;
                }
                yield return pending.Event;
                pending.Acknowledge();
                pending = null;
            }
        }
        finally
        {
            executionCancellation.Cancel();
            try
            {
                await execution.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
            {
                // Early iterator disposal cancels local execution without surfacing disposal as failure.
            }
        }
    }

    private async Task ProduceStreamingEventsAsync(
        AgentRunRequest request,
        ChannelWriter<StreamEnvelope> writer,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var streamedOutputCharacters = 0L;
        try
        {
            await _executionEngine.ExecuteAsync(
                request,
                (modelRequest, token) => StreamModelTurnAsync(
                    modelRequest,
                    (text, callbackToken) => WriteEventAsync(
                        writer,
                        new AssistantTextDelta(text),
                        callbackToken),
                    request.Agent.Options.ResourceLimits?.MaxOutputCharacters,
                    () => streamedOutputCharacters,
                    value => streamedOutputCharacters = value,
                    token),
                "streaming",
                cancellationToken,
                (completed, token) => WriteEventAsync(writer, new AgentRunCompleted(completed), token),
                callerCancellationToken).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
            throw;
        }
    }

    private static async ValueTask WriteEventAsync(
        ChannelWriter<StreamEnvelope> writer,
        AgentRunEvent runEvent,
        CancellationToken cancellationToken)
    {
        var envelope = new StreamEnvelope(runEvent, cancellationToken);
        await writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        await envelope.WaitForAcknowledgementAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelResponse> StreamModelTurnAsync(
        ModelRequest request,
        Func<string, CancellationToken, ValueTask> emitTextAsync,
        long? maxOutputCharacters,
        Func<long> getOutputCharacters,
        Action<long> setOutputCharacters,
        CancellationToken cancellationToken)
    {
        if (_modelClient is not IStreamingAgentModelClient streamingClient)
        {
            throw new AgentExecutionEngine.ModelTurnRejectedException(
                AgentExecutionErrorCode.CapabilityNotSupported,
                "The configured model client does not support streaming.");
        }

        var stream = streamingClient.StreamAsync(request, cancellationToken);
        if (stream is null)
        {
            throw new AgentExecutionEngine.ModelTurnRejectedException(
                AgentExecutionErrorCode.InvalidModelResponse,
                "The model provider returned an invalid stream.");
        }

        ModelResponse? response = null;
        var emittedText = false;
        var providerEvents = 0L;
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { providerEvents = checked(providerEvents + 1); }
            catch (OverflowException exception)
            {
                throw new AgentExecutionEngine.ModelTurnRejectedException(
                    AgentExecutionErrorCode.ProviderProcessingLimitExceeded,
                    "Provider processing exceeded a configured resource bound.", exception);
            }
            if (request.ResourceLimits?.MaxProviderStreamEvents is { } eventLimit && providerEvents > eventLimit)
            {
                throw new AgentExecutionEngine.ModelTurnRejectedException(
                    AgentExecutionErrorCode.ProviderProcessingLimitExceeded,
                    "Provider processing exceeded a configured resource bound.");
            }
            var streamEvent = enumerator.Current;
            if (response is not null)
            {
                throw new AgentExecutionEngine.ModelTurnRejectedException(
                    AgentExecutionErrorCode.InvalidModelResponse,
                    "The model stream contained an event after its terminal completion.");
            }

            switch (streamEvent)
            {
                case ModelTextDelta delta when !string.IsNullOrEmpty(delta.Text):
                    long outputCharacters;
                    try
                    {
                        outputCharacters = checked(getOutputCharacters() + delta.Text.Length);
                    }
                    catch (OverflowException exception)
                    {
                        throw new AgentExecutionEngine.ModelTurnRejectedException(
                            AgentExecutionErrorCode.OutputSizeLimitExceeded,
                            "The accumulated model output exceeded its configured character budget.",
                            exception);
                    }
                    if (maxOutputCharacters is { } maximum && outputCharacters > maximum)
                    {
                        throw new AgentExecutionEngine.ModelTurnRejectedException(
                            AgentExecutionErrorCode.OutputSizeLimitExceeded,
                            "The accumulated model output exceeded its configured character budget.");
                    }
                    setOutputCharacters(outputCharacters);
                    emittedText = true;
                    await emitTextAsync(delta.Text, cancellationToken).ConfigureAwait(false);
                    break;
                case ModelTextDelta:
                    throw new AgentExecutionEngine.ModelTurnRejectedException(
                        AgentExecutionErrorCode.InvalidModelResponse,
                        "The model stream contained an invalid text delta.");
                case ModelResponseCompleted completed when completed.Response is not null:
                    response = completed.Response;
                    break;
                case ModelResponseCompleted:
                case null:
                default:
                    throw new AgentExecutionEngine.ModelTurnRejectedException(
                        AgentExecutionErrorCode.InvalidModelResponse,
                        "The model stream contained an invalid event.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (response is null)
        {
            throw new AgentExecutionEngine.ModelTurnRejectedException(
                AgentExecutionErrorCode.InvalidModelResponse,
                "The model stream ended without a terminal completion.");
        }
        if (emittedText && response.FinishReason == ModelFinishReason.ToolCalls)
        {
            throw new AgentExecutionEngine.ModelTurnRejectedException(
                AgentExecutionErrorCode.InvalidModelResponse,
                "The model stream emitted text for a tool-call response.");
        }

        return response;
    }

    private sealed class StreamEnvelope(AgentRunEvent runEvent, CancellationToken executionToken)
    {
        private readonly TaskCompletionSource _acknowledged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentRunEvent Event { get; } = runEvent;
        public CancellationToken ExecutionToken { get; } = executionToken;

        public void Acknowledge() => _acknowledged.TrySetResult();

        public Task WaitForAcknowledgementAsync(CancellationToken cancellationToken) =>
            _acknowledged.Task.WaitAsync(cancellationToken);
    }
}
