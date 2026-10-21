using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;
using Microsoft.Extensions.Logging;

namespace AgentForge.Core.Tests;

public sealed class StreamingRuntimeTests
{
    [Fact]
    public async Task StreamAsync_TextDeltas_EmitsProvisionalTextThenCompletion()
    {
        var client = Client(Events(
            new ModelTextDelta("hel"),
            new ModelTextDelta("lo"),
            Completed("hello", new ModelUsage(2, 3))));

        var events = await CollectAsync(Runtime(client).StreamAsync(Request()));

        Assert.Collection(events,
            item => Assert.Equal("hel", Assert.IsType<AssistantTextDelta>(item).Text),
            item => Assert.Equal("lo", Assert.IsType<AssistantTextDelta>(item).Text),
            item =>
            {
                var completed = Assert.IsType<AgentRunCompleted>(item);
                Assert.Equal("hello", completed.Result.FinalMessage.Text);
                Assert.Equal(5, completed.Result.Usage?.TotalTokens);
                Assert.Equal(3, completed.Result.Transcript.Count);
            });
    }

    [Fact]
    public async Task StreamAsync_ZeroDeltaCompletion_EmitsOnlyCompletion()
    {
        var events = await CollectAsync(Runtime(Client(Events(Completed("done")))).StreamAsync(Request()));

        Assert.Single(events);
        Assert.Equal("done", Assert.IsType<AgentRunCompleted>(events[0]).Result.FinalMessage.Text);
    }

    [Fact]
    public async Task StreamAsync_AndRunAsync_ShareObservableExecutionPolicy()
    {
        var response = ModelResponse.Completed(AssistantMessage.FromText("done"), new ModelUsage(3, 4));
        var standard = await Runtime(ScriptedAgentModelClient.FromResponses(response)).RunAsync(Request());
        var streamed = Assert.IsType<AgentRunCompleted>((await CollectAsync(
            Runtime(Client(Events(new ModelResponseCompleted(response)))).StreamAsync(Request())))[^1]).Result;

        Assert.Equal(standard.FinalMessage, streamed.FinalMessage);
        Assert.Equal(standard.Transcript, streamed.Transcript);
        Assert.Equal(standard.Usage, streamed.Usage);
        Assert.Equal(standard.ModelCalls, streamed.ModelCalls);
        Assert.Equal(standard.ToolCalls, streamed.ToolCalls);
    }

    [Fact]
    public async Task StreamAsync_ToolRoundTrip_UsesSharedTranscriptAndAccounting()
    {
        var tool = new RecordingTool("echo");
        var client = Client(
            Events(new ModelResponseCompleted(ModelResponse.ToolCalls(
                AssistantMessage.FromToolCalls(Call("one", "echo")), new ModelUsage(4, 1)))),
            (request, token) =>
            {
                Assert.IsType<AssistantMessage>(request.Messages[^2]);
                Assert.IsType<ToolResultMessage>(request.Messages[^1]);
                return Events(new ModelTextDelta("done"), Completed("done", new ModelUsage(2, 2)))(request, token);
            });

        var events = await CollectAsync(Runtime(client, tool).StreamAsync(Request(["echo"])));
        var result = Assert.IsType<AgentRunCompleted>(events[^1]).Result;

        Assert.Equal(2, result.ModelCalls);
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(9, result.Usage?.TotalTokens);
        Assert.Equal(5, result.Transcript.Count);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task StreamAsync_NonStreamingClient_FailsWithCapabilityNotSupportedBeforeInvocation()
    {
        var invoked = false;
        var client = new ScriptedAgentModelClient([(_, _) =>
        {
            invoked = true;
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("unused")));
        }]);

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CollectAsync(Runtime(client).StreamAsync(Request())));

        Assert.Equal(AgentExecutionErrorCode.CapabilityNotSupported, exception.ErrorCode);
        Assert.False(invoked);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("after")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("empty-delta")]
    [InlineData("text-with-tools")]
    public async Task StreamAsync_ProtocolViolation_IsInvalidModelResponse(string scenario)
    {
        var events = scenario switch
        {
            "missing" => new ModelStreamEvent[] { new ModelTextDelta("partial") },
            "duplicate" => [Completed("done"), Completed("again")],
            "after" => [Completed("done"), new ModelTextDelta("late")],
            "unknown" => [new UnknownStreamEvent()],
            "null" => [null!],
            "empty-delta" => [new ModelTextDelta("")],
            _ => [new ModelTextDelta("unsafe"), new ModelResponseCompleted(ModelResponse.ToolCalls(
                AssistantMessage.FromToolCalls(Call("one", "echo"))))]
        };

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CollectAsync(Runtime(Client(Events(events)), new RecordingTool("echo"))
                .StreamAsync(Request(["echo"]))));

        Assert.Equal(AgentExecutionErrorCode.InvalidModelResponse, exception.ErrorCode);
    }

    [Fact]
    public async Task StreamAsync_ProviderThrowsAfterDelta_PreservesCauseAndEmitsNoCompletion()
    {
        var cause = new InvalidOperationException("provider failure");
        var observed = new List<AgentRunEvent>();
        var calls = 0;
        var client = Client((_, token) =>
        {
            calls++;
            return ThrowAfterAsync(new ModelTextDelta("partial"), cause, token);
        });

        var exception = await Assert.ThrowsAsync<AgentProviderException>(async () =>
        {
            await foreach (var item in Runtime(client).StreamAsync(Request()))
                observed.Add(item);
        });

        Assert.Same(cause, exception.InnerException);
        Assert.Single(observed);
        Assert.IsType<AssistantTextDelta>(observed[0]);
        Assert.DoesNotContain(observed, item => item is AgentRunCompleted);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task StreamAsync_ProviderThrowsBeforeDelta_DoesNotRetryOrComplete()
    {
        var cause = new InvalidOperationException("provider failure");
        var calls = 0;
        var observed = new List<AgentRunEvent>();
        var client = Client((_, token) =>
        {
            calls++;
            return ThrowBeforeAsync(cause, token);
        });

        var exception = await Assert.ThrowsAsync<AgentProviderException>(async () =>
        {
            await foreach (var item in Runtime(client).StreamAsync(Request()))
                observed.Add(item);
        });

        Assert.Same(cause, exception.InnerException);
        Assert.Empty(observed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task StreamAsync_ProviderThrowsAfterCompletion_EmitsNoRunCompletion()
    {
        var cause = new InvalidOperationException("trailing failure");
        var client = Client((_, token) => ThrowAfterAsync(Completed("done"), cause, token));

        var exception = await Assert.ThrowsAsync<AgentProviderException>(() =>
            CollectAsync(Runtime(client).StreamAsync(Request())));

        Assert.Same(cause, exception.InnerException);
    }

    [Fact]
    public async Task StreamAsync_MalformedTerminalResponse_IsInvalidModelResponse()
    {
        var malformed = (ModelResponse)RuntimeHelpers.GetUninitializedObject(typeof(ModelResponse));
        var client = Client(Events(new ModelResponseCompleted(malformed)));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CollectAsync(Runtime(client).StreamAsync(Request())));

        Assert.Equal(AgentExecutionErrorCode.InvalidModelResponse, exception.ErrorCode);
    }

    [Fact]
    public async Task StreamAsync_InvalidBatch_ExecutesNoTools()
    {
        var tool = new RecordingTool("echo");
        var client = Client(Events(new ModelResponseCompleted(ModelResponse.ToolCalls(
            AssistantMessage.FromToolCalls(Call("same", "echo"), Call("same", "echo"))))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CollectAsync(Runtime(client, tool).StreamAsync(Request(["echo"]))));

        Assert.Equal(AgentExecutionErrorCode.InvalidModelResponse, exception.ErrorCode);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task StreamAsync_UnauthorizedTool_ExecutesNoTools()
    {
        var tool = new RecordingTool("echo");
        var client = Client(Events(new ModelResponseCompleted(ModelResponse.ToolCalls(
            AssistantMessage.FromToolCalls(Call("one", "echo"))))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CollectAsync(Runtime(client, tool).StreamAsync(Request())));

        Assert.Equal(AgentExecutionErrorCode.ToolNotAllowed, exception.ErrorCode);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task StreamAsync_UnknownAllowedTool_FailsBeforeProviderInvocation()
    {
        var invoked = false;
        var client = Client((_, _) =>
        {
            invoked = true;
            return Events(Completed("unused"))(null!, default);
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CollectAsync(Runtime(client).StreamAsync(Request(["missing"]))));

        Assert.False(invoked);
    }

    [Fact]
    public async Task StreamAsync_DuplicateCallIdAcrossTurns_IsRejected()
    {
        var tool = new RecordingTool("echo");
        var turn = new ModelResponseCompleted(ModelResponse.ToolCalls(
            AssistantMessage.FromToolCalls(Call("same", "echo"))));
        var client = Client(Events(turn), Events(turn));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CollectAsync(Runtime(client, tool).StreamAsync(Request(["echo"]))));

        Assert.Equal(AgentExecutionErrorCode.InvalidModelResponse, exception.ErrorCode);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Theory]
    [InlineData(1, 2, AgentExecutionErrorCode.ModelCallLimitExceeded)]
    [InlineData(2, 1, AgentExecutionErrorCode.ToolCallLimitExceeded)]
    public async Task StreamAsync_Limits_AreEnforced(int maxModels, int maxTools, AgentExecutionErrorCode expected)
    {
        var tool = new RecordingTool("echo");
        var calls = expected == AgentExecutionErrorCode.ToolCallLimitExceeded
            ? new[] { Call("one", "echo"), Call("two", "echo") }
            : [Call("one", "echo")];
        var client = Client(Events(new ModelResponseCompleted(ModelResponse.ToolCalls(
            AssistantMessage.FromToolCalls(calls)))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => CollectAsync(
            Runtime(client, tool).StreamAsync(Request(["echo"], new AgentExecutionLimits(maxModels, maxTools)))));

        Assert.Equal(expected, exception.ErrorCode);
    }

    [Fact]
    public async Task StreamAsync_CancelBeforeFirstEvent_DoesNotComplete()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(
            Runtime(Client(Events(Completed("unused")))).StreamAsync(Request(), cancellation.Token)));
    }

    [Fact]
    public async Task StreamAsync_CancelWhileAwaitingProvider_PropagatesCancellation()
    {
        var entered = Signal();
        var client = Client((_, token) => WaitForCancellationAsync(entered, token));
        using var cancellation = new CancellationTokenSource();
        var run = CollectAsync(Runtime(client).StreamAsync(Request(), cancellation.Token));
        await entered.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task StreamAsync_ProviderIgnoresCancellation_CoreCheckpointStillCancels()
    {
        var entered = Signal();
        var release = Signal();
        var client = Client((_, _) => IgnoreCancellationAsync(entered, release));
        using var cancellation = new CancellationTokenSource();
        var run = CollectAsync(Runtime(client).StreamAsync(Request(), cancellation.Token));
        await entered.Task;

        cancellation.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task StreamAsync_CancelBetweenDeltas_StopsEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        var client = Client(Events(
            new ModelTextDelta("first"),
            new ModelTextDelta("second"),
            Completed("complete")));
        await using var enumerator = Runtime(client).StreamAsync(Request(), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("first", Assert.IsType<AssistantTextDelta>(enumerator.Current).Text);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task StreamAsync_CancelDuringTool_PreventsLaterTool()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new RecordingTool("first", (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Json("null"));
        });
        var second = new RecordingTool("second");
        var client = Client(Events(new ModelResponseCompleted(ModelResponse.ToolCalls(
            AssistantMessage.FromToolCalls(Call("one", "first"), Call("two", "second"))))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(
            Runtime(client, first, second).StreamAsync(Request(["first", "second"]), cancellation.Token)));

        Assert.Equal(1, first.InvocationCount);
        Assert.Equal(0, second.InvocationCount);
    }

    [Fact]
    public async Task StreamAsync_ConsumerStopsAfterDelta_PreventsToolExecution()
    {
        var tool = new RecordingTool("echo");
        var client = Client(Events(
            new ModelTextDelta("partial"),
            new ModelResponseCompleted(ModelResponse.ToolCalls(
                AssistantMessage.FromToolCalls(Call("one", "echo"))))));

        await foreach (var item in Runtime(client, tool).StreamAsync(Request(["echo"])))
        {
            Assert.IsType<AssistantTextDelta>(item);
            break;
        }

        Assert.Equal(0, tool.InvocationCount);
    }

    [Theory]
    [InlineData(ModelFinishReason.LengthLimit, AgentExecutionErrorCode.ModelOutputIncomplete)]
    [InlineData(ModelFinishReason.ContentFiltered, AgentExecutionErrorCode.ModelOutputFiltered)]
    [InlineData(ModelFinishReason.Refused, AgentExecutionErrorCode.ModelRefused)]
    [InlineData(ModelFinishReason.Unknown, AgentExecutionErrorCode.InvalidModelResponse)]
    public async Task StreamAsync_UnsuccessfulFinishReason_FailsWithoutCompletion(
        ModelFinishReason reason,
        AgentExecutionErrorCode expected)
    {
        var client = Client(Events(
            new ModelTextDelta("provisional"),
            new ModelResponseCompleted(new ModelResponse(AssistantMessage.FromText("partial"), reason))));
        var observed = new List<AgentRunEvent>();

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(async () =>
        {
            await foreach (var item in Runtime(client).StreamAsync(Request()))
                observed.Add(item);
        });

        Assert.Equal(expected, exception.ErrorCode);
        Assert.DoesNotContain(observed, item => item is AgentRunCompleted);
    }

    [Fact]
    public async Task StreamAsync_ProviderCancellationWithoutCallerCancellation_IsProviderFailure()
    {
        var response = new ModelResponse(new AssistantMessage(null, []), ModelFinishReason.Cancelled);

        var exception = await Assert.ThrowsAsync<AgentProviderException>(() => CollectAsync(
            Runtime(Client(Events(new ModelResponseCompleted(response)))).StreamAsync(Request())));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task StreamAsync_ConcurrentRuns_IsolateExecutionState()
    {
        var client = new PerRunStreamingClient();
        var runtime = Runtime(client);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
            Assert.IsType<AgentRunCompleted>((await CollectAsync(
                runtime.StreamAsync(Request(message: $"value-{index}"))))[^1]).Result));

        Assert.Equal(12, results.Select(result => result.ExecutionId).Distinct().Count());
        Assert.All(results, result => Assert.Equal(3, result.Transcript.Count));
    }

    [Fact]
    public async Task StreamAsync_UntrustedContent_IsAbsentFromLogsAndExceptionMessage()
    {
        const string secretPrompt = "stream-prompt-secret-91c";
        const string secretOutput = "stream-output-secret-82b";
        var logger = new RecordingLogger<AgentRuntime>();
        var runtime = new AgentRuntime(
            Client(Events(new ModelTextDelta(secretOutput))),
            new AgentToolRegistry([]),
            logger);

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CollectAsync(runtime.StreamAsync(Request(message: secretPrompt))));
        var logs = string.Join(Environment.NewLine, logger.Messages);

        Assert.DoesNotContain(secretPrompt, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(secretOutput, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPrompt, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretOutput, exception.Message, StringComparison.Ordinal);
    }

    private static AgentRuntime Runtime(IAgentModelClient client, params IAgentTool[] tools) =>
        new(client, new AgentToolRegistry(tools));

    private static ScriptedStreamingAgentModelClient Client(
        params Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>>[] turns) => new(turns);

    private static Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>> Events(
        params ModelStreamEvent[] events) => (_, token) => EnumerateAsync(events, token);

    private static async IAsyncEnumerable<ModelStreamEvent> EnumerateAsync(
        IEnumerable<ModelStreamEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ModelStreamEvent> ThrowAfterAsync(
        ModelStreamEvent item,
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return item;
        await Task.Yield();
        throw exception;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> ThrowBeforeAsync(
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested)
            yield break;
        throw exception;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> WaitForCancellationAsync(
        TaskCompletionSource entered,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        entered.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> IgnoreCancellationAsync(
        TaskCompletionSource entered,
        TaskCompletionSource release)
    {
        entered.SetResult();
        await release.Task;
        yield return Completed("must not complete");
    }

    private static async Task<List<AgentRunEvent>> CollectAsync(IAsyncEnumerable<AgentRunEvent> events)
    {
        var result = new List<AgentRunEvent>();
        await foreach (var item in events)
            result.Add(item);
        return result;
    }

    private static AgentRunRequest Request(
        IEnumerable<string>? allowedTools = null,
        AgentExecutionLimits? limits = null,
        string message = "hello") => new(
            new AgentDefinition(
                new AgentId("stream-agent"),
                "instructions",
                new ModelId("stream-model"),
                allowedTools ?? [],
                limits ?? new AgentExecutionLimits()),
            [new UserMessage(message)]);

    private static ModelResponseCompleted Completed(string text, ModelUsage? usage = null) =>
        new(ModelResponse.Completed(AssistantMessage.FromText(text), usage));

    private static ToolCall Call(string id, string name) => new(id, name, Json("{}"));
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record UnknownStreamEvent : ModelStreamEvent;

    private sealed class RecordingTool : IAgentTool
    {
        private readonly Func<ToolInvocation, CancellationToken, Task<JsonElement>> _invoke;
        private int _invocationCount;

        public RecordingTool(
            string name,
            Func<ToolInvocation, CancellationToken, Task<JsonElement>>? invoke = null)
        {
            Descriptor = new ToolDescriptor(name, "test", Json("{\"type\":\"object\"}"));
            _invoke = invoke ?? ((_, _) => Task.FromResult(Json("null")));
        }

        public ToolDescriptor Descriptor { get; }
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return new ToolResult(await _invoke(invocation, cancellationToken));
        }
    }

    private sealed class PerRunStreamingClient : IAgentModelClient, IStreamingAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("unused")));

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = request.Messages.OfType<UserMessage>().Single().Text;
            yield return new ModelTextDelta(text);
            await Task.Yield();
            yield return Completed(text);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
