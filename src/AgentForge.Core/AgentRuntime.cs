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
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        ArgumentNullException.ThrowIfNull(toolRegistry);
        _executionEngine = new AgentExecutionEngine(toolRegistry, logger ?? NullLogger<AgentRuntime>.Instance);
    }

    /// <inheritdoc />
    public Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default) =>
        _executionEngine.ExecuteAsync(
            request,
            (modelRequest, token) => _modelClient.CompleteAsync(modelRequest, token),
            cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentRunEvent> StreamAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = Channel.CreateUnbounded<StreamEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var execution = ProduceStreamingEventsAsync(
            request,
            events.Writer,
            executionCancellation.Token);

        try
        {
            StreamEnvelope? pending = null;
            while (await events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!events.Reader.TryRead(out pending))
                    continue;
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
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _executionEngine.ExecuteAsync(
                request,
                (modelRequest, token) => StreamModelTurnAsync(
                    modelRequest,
                    (text, callbackToken) => WriteEventAsync(
                        writer,
                        new AssistantTextDelta(text),
                        callbackToken),
                    token),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteEventAsync(writer, new AgentRunCompleted(result), cancellationToken).ConfigureAwait(false);
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
        var envelope = new StreamEnvelope(runEvent);
        await writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        await envelope.WaitForAcknowledgementAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelResponse> StreamModelTurnAsync(
        ModelRequest request,
        Func<string, CancellationToken, ValueTask> emitTextAsync,
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
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class StreamEnvelope(AgentRunEvent runEvent)
    {
        private readonly TaskCompletionSource _acknowledged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentRunEvent Event { get; } = runEvent;

        public void Acknowledge() => _acknowledged.TrySetResult();

        public Task WaitForAcknowledgementAsync(CancellationToken cancellationToken) =>
            _acknowledged.Task.WaitAsync(cancellationToken);
    }
}
