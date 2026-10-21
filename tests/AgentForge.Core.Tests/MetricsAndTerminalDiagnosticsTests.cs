using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;

namespace AgentForge.Core.Tests;

[Collection("Activity instrumentation")]
public sealed class MetricsAndTerminalDiagnosticsTests
{
    [Fact]
    public async Task RunAsync_NoListeners_PreservesBehavior()
    {
        var result = await Runtime(CompletedClient()).RunAsync(Request());

        Assert.Equal("done", result.FinalMessage.Text);
    }

    [Fact]
    public async Task RunAsync_ThrowingTelemetryListeners_DoNotChangeBehavior()
    {
        using var metricListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "AgentForge") listener.EnableMeasurementEvents(instrument);
            }
        };
        metricListener.SetMeasurementEventCallback<long>(static (_, _, _, _) => throw new InvalidOperationException("listener"));
        metricListener.SetMeasurementEventCallback<double>(static (_, _, _, _) => throw new InvalidOperationException("listener"));
        metricListener.Start();
        using var diagnostics = new ThrowingDiagnosticObserver();

        var result = await Runtime(CompletedClient()).RunAsync(Request());

        Assert.Equal("done", result.FinalMessage.Text);
    }

    [Fact]
    public async Task RunAsync_Success_RecordsExecutionCountAndDuration()
    {
        using var metrics = new MetricRecorder();
        var time = new ManualTimeProvider();
        var client = new ScriptedAgentModelClient([(_, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(ModelResponse.Completed(new AssistantMessage("done", [])));
        }]);

        await Runtime(client, time).RunAsync(Request());

        Assert.Single(metrics.For("agentforge.agent.executions"));
        Assert.Equal(2, metrics.Single("agentforge.agent.execution.duration").Value);
        Assert.Equal("success", metrics.Single("agentforge.agent.execution.duration").Tag("agentforge.outcome"));
    }

    [Fact]
    public async Task StreamAsync_Success_RecordsEnumerationCountAndDuration()
    {
        using var metrics = new MetricRecorder();
        var time = new ManualTimeProvider();
        var client = new ScriptedStreamingAgentModelClient([(_, token) => TimedStream(time, token)]);

        await foreach (var _ in Runtime(client, time).StreamAsync(Request())) { }

        Assert.Single(metrics.For("agentforge.agent.executions"));
        Assert.Equal("streaming", metrics.Single("agentforge.agent.execution.duration").Tag("agentforge.operation.mode"));
        Assert.Equal(3, metrics.Single("agentforge.agent.execution.duration").Value);
    }

    [Fact]
    public async Task RunAsync_ModelInvocation_RecordsStartedOperationAndDuration()
    {
        using var metrics = new MetricRecorder();

        await Runtime(CompletedClient()).RunAsync(Request());

        Assert.Single(metrics.For("agentforge.model.invocations"));
        Assert.Single(metrics.For("agentforge.model.invocation.duration"));
    }

    [Fact]
    public async Task RunAsync_ToolInvocation_RecordsStartedOperationAndDuration()
    {
        using var metrics = new MetricRecorder();
        var call = new ToolCall("call", "tool", Json("{}"));
        var client = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(new AssistantMessage(null, [call])),
            ModelResponse.Completed(new AssistantMessage("done", [])));

        await Runtime(client, new DelegateTool("tool", _ => Task.FromResult(new ToolResult(Json("{}")))))
            .RunAsync(Request(["tool"]));

        Assert.Single(metrics.For("agentforge.tool.invocations"));
        Assert.Single(metrics.For("agentforge.tool.invocation.duration"));
    }

    [Fact]
    public async Task RunAsync_ProviderFailure_RecordsOneTerminalFailure()
    {
        using var metrics = new MetricRecorder();
        var client = new ScriptedAgentModelClient([(_, _) => Task.FromException<ModelResponse>(
            new ModelProviderException(ModelProviderErrorKind.Transport, true, "secret"))]);

        await Assert.ThrowsAsync<AgentProviderException>(() => Runtime(client).RunAsync(Request()));

        var failure = metrics.Single("agentforge.agent.failures");
        Assert.Equal("provider_failed", failure.Tag("agentforge.outcome"));
        Assert.Equal("transport", failure.Tag("agentforge.provider.error.kind"));
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_IsDistinct()
    {
        using var metrics = new MetricRecorder();
        using var diagnostics = new DiagnosticRecorder();
        using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedAgentModelClient([async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ModelResponse.Completed(new AssistantMessage("no", []));
        }]);
        var run = Runtime(client).RunAsync(Request(), source.Token);
        await entered.Task;
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Empty(metrics.For("agentforge.agent.failures"));
        Assert.Equal("caller_cancelled", diagnostics.Single().ErrorCode);
    }

    [Fact]
    public async Task RunAsync_RuntimeTimeout_IsDistinct()
    {
        using var diagnostics = new DiagnosticRecorder();
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedAgentModelClient([async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ModelResponse.Completed(new AssistantMessage("no", []));
        }]);
        var run = Runtime(client, time).RunAsync(Request(options:
            new AgentExecutionOptions(TimeSpan.FromSeconds(1))));
        await entered.Task;
        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<AgentExecutionException>(() => run);

        Assert.Equal("executiontimedout", diagnostics.Single().ErrorCode);
        Assert.Equal("timeout", diagnostics.Single().TerminalOutcome);
    }

    [Fact]
    public async Task RunAsync_ResourceLimitFailure_RecordsRejectedDiagnostic()
    {
        using var diagnostics = new DiagnosticRecorder();
        var options = new AgentExecutionOptions(resourceLimits: new AgentResourceLimits(maxInputCharacters: 1));

        await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(CompletedClient()).RunAsync(Request(options: options)));

        Assert.Equal("inputsizelimitexceeded", diagnostics.Single().ErrorCode);
        Assert.Equal(0, diagnostics.Single().ModelInvocationCount);
    }

    [Fact]
    public async Task RunAsync_ProviderProcessingFailure_RecordsClassification()
    {
        using var diagnostics = new DiagnosticRecorder();
        var client = new ScriptedAgentModelClient([(_, _) => Task.FromException<ModelResponse>(
            new ModelProviderException(ModelProviderErrorKind.ProviderProcessingLimitExceeded, false, "secret"))]);

        await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(client).RunAsync(Request()));

        Assert.Equal("providerprocessinglimitexceeded", diagnostics.Single().ErrorCode);
        Assert.Equal("providerprocessinglimitexceeded", diagnostics.Single().ProviderFailureKind);
    }

    [Fact]
    public async Task RunAsync_TokenBudgetFailure_RecordsObservedUsageAndFailure()
    {
        using var metrics = new MetricRecorder();
        var client = ScriptedAgentModelClient.FromResponses(
            ModelResponse.Completed(new AssistantMessage("done", []), new ModelUsage(3, 2)));

        await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(client).RunAsync(Request(options:
            new AgentExecutionOptions(tokenBudget: new AgentTokenBudget(maxTotalTokens: 4)))));

        Assert.Equal(3, metrics.For("agentforge.model.tokens").Single(x => x.Tag("agentforge.token.type") == "input").Value);
        Assert.Equal("tokenbudgetexceeded", metrics.Single("agentforge.agent.failures").Tag("agentforge.error.code"));
    }

    [Fact]
    public async Task RunAsync_AuthoritativeUsage_RecordsInputAndOutputOnce()
    {
        using var metrics = new MetricRecorder();
        var client = ScriptedAgentModelClient.FromResponses(
            ModelResponse.Completed(new AssistantMessage("done", []), new ModelUsage(4, 2)));

        await Runtime(client).RunAsync(Request());

        Assert.Equal([4d, 2d], metrics.For("agentforge.model.tokens").Select(x => x.Value));
    }

    [Fact]
    public async Task StreamAsync_Deltas_DoNotDuplicateUsage()
    {
        using var metrics = new MetricRecorder();
        var client = ScriptedStreamingAgentModelClient.FromEvents([
            new ModelTextDelta("a"), new ModelTextDelta("b"),
            new ModelResponseCompleted(ModelResponse.Completed(new AssistantMessage("ab", []), new ModelUsage(5, 1)))]);

        await foreach (var _ in Runtime(client).StreamAsync(Request())) { }

        Assert.Equal(2, metrics.For("agentforge.model.tokens").Count);
        Assert.Equal(6, metrics.For("agentforge.model.tokens").Sum(x => x.Value));
    }

    [Fact]
    public async Task StreamAsync_FailureAfterProvisionalOutput_RecordsFailureOnly()
    {
        using var metrics = new MetricRecorder();
        using var diagnostics = new DiagnosticRecorder();
        var client = new ScriptedStreamingAgentModelClient([(_, token) => FailingStream(token)]);

        await Assert.ThrowsAsync<AgentProviderException>(async () =>
        {
            await foreach (var _ in Runtime(client).StreamAsync(Request())) { }
        });

        Assert.Equal("provider_failed", diagnostics.Single().TerminalOutcome);
        Assert.DoesNotContain(metrics.For("agentforge.agent.execution.duration"),
            item => item.Tag("agentforge.outcome") == "success");
    }

    [Fact]
    public async Task StreamAsync_EarlyDisposal_RecordsDistinctTerminalState()
    {
        using var metrics = new MetricRecorder();
        using var diagnostics = new DiagnosticRecorder();
        var client = new ScriptedStreamingAgentModelClient([(_, token) => NeverEndingStream(token)]);
        await using (var enumerator = Runtime(client).StreamAsync(Request()).GetAsyncEnumerator())
            Assert.True(await enumerator.MoveNextAsync());

        Assert.Equal("stream_disposed", diagnostics.Single().ErrorCode);
        Assert.Equal("cancelled", metrics.Single("agentforge.agent.execution.duration").Tag("agentforge.outcome"));
        Assert.Empty(metrics.For("agentforge.agent.failures"));
    }

    [Fact]
    public async Task StreamAsync_TimeoutWithResourceLimits_RecordsOneTimeoutTerminalState()
    {
        using var metrics = new MetricRecorder();
        using var diagnostics = new DiagnosticRecorder();
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedStreamingAgentModelClient([(_, token) => WaitingStream(entered, token)]);
        var options = new AgentExecutionOptions(
            TimeSpan.FromSeconds(1),
            new AgentResourceLimits(maxOutputCharacters: 32, maxProviderStreamEvents: 4));
        var run = ConsumeAsync(Runtime(client, time).StreamAsync(Request(options: options)));
        await entered.Task;

        time.Advance(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => run);
        Assert.Equal(AgentExecutionErrorCode.ExecutionTimedOut, exception.ErrorCode);
        Assert.Equal("timeout", diagnostics.Single().TerminalOutcome);
        Assert.Single(metrics.For("agentforge.agent.failures"));
        Assert.Single(metrics.For("agentforge.agent.execution.duration"));
    }

    [Fact]
    public async Task StreamAsync_TokenBudgetFailureAfterDelta_DoesNotCompleteSuccessfully()
    {
        using var metrics = new MetricRecorder();
        using var diagnostics = new DiagnosticRecorder();
        var client = ScriptedStreamingAgentModelClient.FromEvents([
            new ModelTextDelta("provisional"),
            new ModelResponseCompleted(ModelResponse.Completed(
                new AssistantMessage("provisional", []), new ModelUsage(3, 3)))]);
        var observed = new List<AgentRunEvent>();

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(async () =>
        {
            await foreach (var item in Runtime(client).StreamAsync(Request(options:
                new AgentExecutionOptions(tokenBudget: new AgentTokenBudget(maxTotalTokens: 5)))))
                observed.Add(item);
        });

        Assert.Equal(AgentExecutionErrorCode.TokenBudgetExceeded, exception.ErrorCode);
        Assert.Single(observed, item => item is AssistantTextDelta);
        Assert.DoesNotContain(observed, item => item is AgentRunCompleted);
        Assert.Equal("tokenbudgetexceeded", diagnostics.Single().ErrorCode);
        Assert.Single(metrics.For("agentforge.agent.failures"));
        Assert.DoesNotContain(metrics.For("agentforge.agent.execution.duration"),
            item => item.Tag("agentforge.outcome") == "success");
    }

    [Fact]
    public async Task RunAsync_ProviderFailureWithTracingAndMetrics_EmitsOneConsistentTerminalRecord()
    {
        using var activities = new ActivityRecorder();
        using var metrics = new MetricRecorder();
        using var diagnostics = new DiagnosticRecorder();
        var client = new ScriptedAgentModelClient([(_, _) => Task.FromException<ModelResponse>(
            new ModelProviderException(ModelProviderErrorKind.ServiceUnavailable, true, "sensitive-provider-text"))]);

        await Assert.ThrowsAsync<AgentProviderException>(() => Runtime(client).RunAsync(Request()));

        Assert.Equal("provider_failed", activities.Single("agentforge.agent.execute").Tag("agentforge.outcome"));
        Assert.Equal("serviceunavailable", metrics.Single("agentforge.agent.failures").Tag("agentforge.provider.error.kind"));
        Assert.Equal("serviceunavailable", diagnostics.Single().ProviderFailureKind);
        Assert.Single(metrics.For("agentforge.agent.execution.duration"));
    }

    [Fact]
    public async Task RunAsync_MultipleTurnsWithResourceAndTokenBudgets_UsesExecutionWideTotals()
    {
        using var diagnostics = new DiagnosticRecorder();
        var call = new ToolCall("call", "tool", Json("{}"));
        var client = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(new AssistantMessage(null, [call]), new ModelUsage(2, 1)),
            ModelResponse.Completed(new AssistantMessage("done", []), new ModelUsage(3, 2)));
        var options = new AgentExecutionOptions(
            null,
            new AgentResourceLimits(maxInputCharacters: 128, maxOutputCharacters: 4,
                maxToolArgumentBytes: 2, maxToolResultBytes: 2),
            new AgentTokenBudget(maxInputTokens: 5, maxOutputTokens: 3, maxTotalTokens: 8));

        var result = await Runtime(client, new DelegateTool("tool", _ =>
                Task.FromResult(new ToolResult(Json("{}")))))
            .RunAsync(Request(["tool"], options));

        Assert.Equal(2, result.ModelCalls);
        Assert.Equal(new ModelUsage(5, 3), result.Usage);
        Assert.Equal(5, diagnostics.Single().ObservedInputTokens);
        Assert.Equal(3, diagnostics.Single().ObservedOutputTokens);
    }

    [Fact]
    public async Task StreamAsync_EarlyDisposalWithTraceAndMetricListeners_ClosesEachSurfaceOnce()
    {
        using var activities = new ActivityRecorder();
        using var metrics = new MetricRecorder();
        using var diagnostics = new DiagnosticRecorder();
        var client = new ScriptedStreamingAgentModelClient([(_, token) => NeverEndingStream(token)]);

        await using (var enumerator = Runtime(client).StreamAsync(Request()).GetAsyncEnumerator())
            Assert.True(await enumerator.MoveNextAsync());

        Assert.Equal("cancelled", activities.Single("agentforge.agent.execute").Tag("agentforge.outcome"));
        Assert.Single(metrics.For("agentforge.agent.execution.duration"));
        Assert.Empty(metrics.For("agentforge.agent.failures"));
        Assert.Equal("stream_disposed", diagnostics.Single().ErrorCode);
    }

    [Fact]
    public async Task RunAsync_TerminalState_EmitsExactlyOneDiagnostic()
    {
        using var diagnostics = new DiagnosticRecorder();

        await Runtime(CompletedClient()).RunAsync(Request());

        Assert.Single(diagnostics.Items);
        Assert.Equal("success", diagnostics.Single().TerminalOutcome);
    }

    [Fact]
    public async Task RunAsync_ConcurrentExecutions_KeepDiagnosticCountsIsolated()
    {
        using var diagnostics = new DiagnosticRecorder();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ScriptedAgentModelClient([async (_, _) =>
        {
            firstEntered.SetResult();
            await release.Task;
            return ModelResponse.Completed(new AssistantMessage("one", []), new ModelUsage(1, 2));
        }]);
        var firstRun = Runtime(first).RunAsync(Request());
        await firstEntered.Task;
        await Runtime(CompletedClient()).RunAsync(Request());
        release.SetResult();
        await firstRun;

        Assert.Equal(2, diagnostics.Items.Count);
        Assert.Contains(diagnostics.Items, item => item.ObservedInputTokens == 1 && item.ObservedOutputTokens == 2);
        Assert.Contains(diagnostics.Items, item => item.ObservedInputTokens is null && item.ObservedOutputTokens is null);
    }

    [Fact]
    public async Task RunAsync_SensitiveData_IsExcludedFromMetricsAndDiagnostics()
    {
        using var metrics = new MetricRecorder();
        using var diagnostics = new DiagnosticRecorder();
        const string secret = "sensitive-83917";
        var request = new AgentRunRequest(
            new AgentDefinition(new AgentId(secret), secret, new ModelId(secret), [], new()),
            [new UserMessage(secret)]);

        await Runtime(CompletedClient()).RunAsync(request);

        var serialized = string.Join('|', metrics.Items.SelectMany(x => x.Tags).Select(x => $"{x.Key}={x.Value}"))
            + JsonSerializer.Serialize(diagnostics.Items);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Failure_DoesNotRecordSuccessfulExecutionCompletion()
    {
        using var metrics = new MetricRecorder();
        var client = new ScriptedAgentModelClient([(_, _) => Task.FromException<ModelResponse>(new InvalidOperationException())]);

        await Assert.ThrowsAsync<AgentProviderException>(() => Runtime(client).RunAsync(Request()));

        Assert.Single(metrics.For("agentforge.agent.execution.duration"));
        Assert.NotEqual("success", metrics.Single("agentforge.agent.execution.duration").Tag("agentforge.outcome"));
    }

    [Fact]
    public async Task RunAsync_MetricsListener_PreservesActivityHierarchy()
    {
        using var metrics = new MetricRecorder();
        using var activities = new ActivityRecorder();

        await Runtime(CompletedClient()).RunAsync(Request());

        var execution = activities.Single("agentforge.agent.execute");
        Assert.Equal(execution.SpanId, activities.Single("agentforge.model.invoke").ParentSpanId);
    }

    private static ScriptedAgentModelClient CompletedClient() =>
        ScriptedAgentModelClient.FromResponses(ModelResponse.Completed(new AssistantMessage("done", [])));

    private static AgentRuntime Runtime(IAgentModelClient client, params IAgentTool[] tools) =>
        new(client, new AgentToolRegistry(tools));

    private static AgentRuntime Runtime(IAgentModelClient client, TimeProvider timeProvider, params IAgentTool[] tools) =>
        new(client, new AgentToolRegistry(tools), timeProvider);

    private static AgentRunRequest Request(IReadOnlyList<string>? tools = null, AgentExecutionOptions? options = null) =>
        new(new AgentDefinition(new AgentId("agent"), "instructions", new ModelId("model"), tools ?? [], new(),
            options ?? AgentExecutionOptions.None), [new UserMessage("hello")]);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async IAsyncEnumerable<ModelStreamEvent> TimedStream(
        ManualTimeProvider time, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        time.Advance(TimeSpan.FromSeconds(3));
        yield return new ModelTextDelta("done");
        yield return new ModelResponseCompleted(ModelResponse.Completed(new AssistantMessage("done", [])));
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> FailingStream(
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        yield return new ModelTextDelta("partial");
        await Task.Yield();
        throw new ModelProviderException(ModelProviderErrorKind.Transport, true, "secret");
    }

    private static async IAsyncEnumerable<ModelStreamEvent> NeverEndingStream(
        [EnumeratorCancellation] CancellationToken token)
    {
        yield return new ModelTextDelta("partial");
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }

    private static async IAsyncEnumerable<ModelStreamEvent> WaitingStream(
        TaskCompletionSource entered,
        [EnumeratorCancellation] CancellationToken token)
    {
        entered.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        yield break;
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<AgentRunEvent> events)
    {
        await foreach (var _ in events) { }
    }

    private sealed class DelegateTool(string name, Func<CancellationToken, Task<ToolResult>> invoke) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(name, "description", Json("{\"type\":\"object\"}"));
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            invoke(cancellationToken);
    }

    private sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public string? Tag(string name) => Tags.GetValueOrDefault(name)?.ToString();
    }

    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Measurement> _items = new();

        public MetricRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "AgentForge") listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Items => _items.ToArray();
        public IReadOnlyList<Measurement> For(string name) => Items.Where(x => x.Name == name).ToArray();
        public Measurement Single(string name) => Assert.Single(For(name));

        private void Add<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct =>
            _items.Enqueue(new Measurement(instrument.Name, Convert.ToDouble(value), tags.ToArray().ToDictionary()));

        public void Dispose() => _listener.Dispose();
    }

    private sealed class DiagnosticRecorder : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _allSubscription;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly ConcurrentQueue<AgentExecutionDiagnostic> _items = new();

        public DiagnosticRecorder() => _allSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        public IReadOnlyList<AgentExecutionDiagnostic> Items => _items.ToArray();
        public AgentExecutionDiagnostic Single() => Assert.Single(Items);
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "AgentForge") _subscriptions.Add(value.Subscribe(this, name => name == "AgentForge.Execution.Terminal"));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is AgentExecutionDiagnostic diagnostic) _items.Enqueue(diagnostic);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _allSubscription.Dispose();
        }
    }

    private sealed class ActivityRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<Activity> _activities = new();
        public ActivityRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "AgentForge",
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity => _activities.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }
        public Activity Single(string name) => Assert.Single(_activities, x => x.OperationName == name);
        public void Dispose() => _listener.Dispose();
    }

    private sealed class ThrowingDiagnosticObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _allSubscription;
        private readonly List<IDisposable> _subscriptions = [];
        public ThrowingDiagnosticObserver() => _allSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "AgentForge") _subscriptions.Add(value.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value) => throw new InvalidOperationException("listener");
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _allSubscription.Dispose();
        }
    }
}
