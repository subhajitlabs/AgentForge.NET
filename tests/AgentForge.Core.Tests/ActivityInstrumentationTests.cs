using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;

namespace AgentForge.Core.Tests;

[Collection("Activity instrumentation")]
public sealed class ActivityInstrumentationTests
{
    private const string SourceName = "AgentForge";

    [Fact]
    public async Task RunAsync_NoListener_DoesNotCreateAnActivity()
    {
        var prior = Activity.Current;
        var result = await Runtime(CompletedClient()).RunAsync(Request());

        Assert.Equal("done", result.FinalMessage.Text);
        Assert.Same(prior, Activity.Current);
    }

    [Fact]
    public async Task RunAsync_Success_EmitsExecutionAndModelHierarchy()
    {
        using var recorder = new ActivityRecorder();
        using var parent = new Activity("test-parent").Start();

        await Runtime(CompletedClient()).RunAsync(Request());

        var execution = recorder.Single("agentforge.agent.execute");
        var model = recorder.Single("agentforge.model.invoke");
        Assert.Equal(parent.TraceId, execution.TraceId);
        Assert.Equal(parent.SpanId, execution.ParentSpanId);
        Assert.Equal(execution.SpanId, model.ParentSpanId);
        Assert.Equal("buffered", execution.Tag("agentforge.operation.mode"));
        Assert.Equal("success", execution.Tag("agentforge.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, execution.Status);
    }

    [Fact]
    public async Task StreamAsync_Success_EmitsStreamingExecutionAndModelHierarchy()
    {
        using var recorder = new ActivityRecorder();
        var runtime = Runtime(ScriptedStreamingAgentModelClient.FromEvents([
            new ModelTextDelta("do"),
            new ModelResponseCompleted(ModelResponse.Completed(new AssistantMessage("done", [])))]));

        await foreach (var _ in runtime.StreamAsync(Request())) { }

        var execution = recorder.Single("agentforge.agent.execute");
        var model = recorder.Single("agentforge.model.invoke");
        Assert.Equal(execution.SpanId, model.ParentSpanId);
        Assert.Equal("streaming", execution.Tag("agentforge.operation.mode"));
        Assert.Equal("streaming", model.Tag("agentforge.operation.mode"));
        Assert.Equal("success", execution.Tag("agentforge.outcome"));
    }

    [Fact]
    public async Task RunAsync_ToolRoundTrip_EmitsOrderedModelAndToolChildren()
    {
        using var recorder = new ActivityRecorder();
        var call = new ToolCall("call-secret", "safe_tool", Json("{}"));
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(new AssistantMessage(null, [call])),
            ModelResponse.Completed(new AssistantMessage("done", [])));

        await Runtime(model, new DelegateTool("safe_tool", _ => Task.FromResult(new ToolResult(Json("{\"ok\":true}")))))
            .RunAsync(Request(["safe_tool"]));

        var execution = recorder.Single("agentforge.agent.execute");
        var children = recorder.Activities.Where(activity => activity.ParentSpanId == execution.SpanId).ToArray();
        Assert.Equal(["agentforge.model.invoke", "agentforge.tool.invoke", "agentforge.model.invoke"],
            children.OrderBy(activity => activity.StartTimeUtc).Select(activity => activity.OperationName));
        Assert.Equal("1", children[0].Tag("agentforge.model.turn"));
        Assert.Equal("1", children[1].Tag("agentforge.tool.invocation"));
        Assert.Equal("2", children[2].Tag("agentforge.model.turn"));
    }

    [Fact]
    public async Task RunAsync_ProviderFailure_RecordsSanitizedClassification()
    {
        using var recorder = new ActivityRecorder();
        var secret = "provider-secret-message";
        var client = new ScriptedAgentModelClient([(_, _) => Task.FromException<ModelResponse>(
            new ModelProviderException(ModelProviderErrorKind.RateLimited, true, secret))]);

        await Assert.ThrowsAsync<AgentProviderException>(() => Runtime(client).RunAsync(Request()));

        AssertFailure(recorder.Single("agentforge.agent.execute"), "provider_failed", "provider_failed");
        var model = recorder.Single("agentforge.model.invoke");
        Assert.Equal("ratelimited", model.Tag("agentforge.provider.error.kind"));
        Assert.Equal("True", model.Tag("agentforge.provider.is_transient"));
        Assert.DoesNotContain(secret, recorder.Serialized(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ToolFailure_RecordsToolFailure()
    {
        using var recorder = new ActivityRecorder();
        var call = new ToolCall("call", "failing_tool", Json("{}"));
        var client = ScriptedAgentModelClient.FromResponses(ModelResponse.ToolCalls(new AssistantMessage(null, [call])));

        await Assert.ThrowsAsync<AgentToolException>(() => Runtime(client,
            new DelegateTool("failing_tool", _ => Task.FromException<ToolResult>(new InvalidOperationException("tool-secret"))))
            .RunAsync(Request(["failing_tool"])));

        AssertFailure(recorder.Single("agentforge.tool.invoke"), "tool_failed", "tool_failed");
        AssertFailure(recorder.Single("agentforge.agent.execute"), "tool_failed", "tool_failed");
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_RecordsCancellationWithoutErrorStatus()
    {
        using var recorder = new ActivityRecorder();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedAgentModelClient([async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ModelResponse.Completed(new AssistantMessage("unreachable", []));
        }]);
        var run = Runtime(client).RunAsync(Request(), cancellation.Token);
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var execution = recorder.Single("agentforge.agent.execute");
        Assert.Equal("cancelled", execution.Tag("agentforge.outcome"));
        Assert.Equal("caller_cancelled", execution.Tag("agentforge.error.code"));
        Assert.Equal(ActivityStatusCode.Unset, execution.Status);
    }

    [Fact]
    public async Task RunAsync_RuntimeTimeout_RecordsDistinctTimeout()
    {
        using var recorder = new ActivityRecorder();
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedAgentModelClient([async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ModelResponse.Completed(new AssistantMessage("unreachable", []));
        }]);
        var run = Runtime(client, time).RunAsync(Request(options: new AgentExecutionOptions(TimeSpan.FromSeconds(1))));
        await entered.Task;
        time.Advance(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => run);
        Assert.Equal(AgentExecutionErrorCode.ExecutionTimedOut, exception.ErrorCode);
        AssertFailure(recorder.Single("agentforge.agent.execute"), "timeout", "executiontimedout");
        AssertFailure(recorder.Single("agentforge.model.invoke"), "timeout", "executiontimedout");
    }

    [Fact]
    public async Task RunAsync_ResourceBudgetFailure_RecordsPolicyRejectionWithoutModelActivity()
    {
        using var recorder = new ActivityRecorder();
        var options = new AgentExecutionOptions(resourceLimits: new AgentResourceLimits(maxInputCharacters: 1));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            Runtime(CompletedClient()).RunAsync(Request(options: options)));

        Assert.Equal(AgentExecutionErrorCode.InputSizeLimitExceeded, exception.ErrorCode);
        AssertFailure(recorder.Single("agentforge.agent.execute"), "rejected", "inputsizelimitexceeded");
        Assert.DoesNotContain(recorder.Activities, activity => activity.OperationName == "agentforge.model.invoke");
    }

    [Fact]
    public async Task RunAsync_ProviderProcessingFailure_RecordsStableCode()
    {
        using var recorder = new ActivityRecorder();
        var client = new ScriptedAgentModelClient([(_, _) => Task.FromException<ModelResponse>(
            new ModelProviderException(ModelProviderErrorKind.ProviderProcessingLimitExceeded, false, "safe"))]);

        await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(client).RunAsync(Request()));

        AssertFailure(recorder.Single("agentforge.agent.execute"), "rejected", "providerprocessinglimitexceeded");
        Assert.Equal("providerprocessinglimitexceeded",
            recorder.Single("agentforge.model.invoke").Tag("agentforge.provider.error.kind"));
    }

    [Fact]
    public async Task RunAsync_TokenBudgetFailure_RecordsStableCode()
    {
        using var recorder = new ActivityRecorder();
        var client = ScriptedAgentModelClient.FromResponses(
            ModelResponse.Completed(new AssistantMessage("done", []), new ModelUsage(2, 3)));
        var options = new AgentExecutionOptions(tokenBudget: new AgentTokenBudget(maxTotalTokens: 4));

        await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(client).RunAsync(Request(options: options)));

        AssertFailure(recorder.Single("agentforge.agent.execute"), "rejected", "tokenbudgetexceeded");
    }

    [Fact]
    public async Task StreamAsync_FailureAfterDelta_ClosesActivitiesAsProviderFailure()
    {
        using var recorder = new ActivityRecorder();
        var client = new ScriptedStreamingAgentModelClient([(_, token) => FailingStream(token)]);
        var observed = new List<AgentRunEvent>();

        await Assert.ThrowsAsync<AgentProviderException>(async () =>
        {
            await foreach (var item in Runtime(client).StreamAsync(Request())) observed.Add(item);
        });

        Assert.Single(observed, item => item is AssistantTextDelta);
        AssertFailure(recorder.Single("agentforge.model.invoke"), "provider_failed", "provider_failed");
        AssertFailure(recorder.Single("agentforge.agent.execute"), "provider_failed", "provider_failed");
    }

    [Fact]
    public async Task StreamAsync_EarlyDisposal_ClosesExecutionAndModelActivities()
    {
        using var recorder = new ActivityRecorder();
        var client = new ScriptedStreamingAgentModelClient([(_, token) => NeverEndingStream(token)]);
        await using (var enumerator = Runtime(client).StreamAsync(Request()).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        Assert.Equal("cancelled", recorder.Single("agentforge.agent.execute").Tag("agentforge.outcome"));
        Assert.Equal("cancelled", recorder.Single("agentforge.model.invoke").Tag("agentforge.outcome"));
    }

    [Fact]
    public async Task RunAsync_ConcurrentExecutions_PreserveTraceIsolation()
    {
        using var recorder = new ActivityRecorder();
        async Task<ActivityTraceId> RunUnderParentAsync(string name)
        {
            using var parent = new Activity(name).Start();
            var traceId = parent.TraceId;
            await Runtime(CompletedClient()).RunAsync(Request());
            return traceId;
        }

        var traceIds = await Task.WhenAll(RunUnderParentAsync("one"), RunUnderParentAsync("two"));
        var executions = recorder.Activities.Where(activity => activity.OperationName == "agentforge.agent.execute").ToArray();

        Assert.Equal(2, executions.Length);
        Assert.NotEqual(traceIds[0], traceIds[1]);
        Assert.Contains(executions, activity => activity.TraceId == traceIds[0]);
        Assert.Contains(executions, activity => activity.TraceId == traceIds[1]);
        Assert.All(recorder.Activities.Where(activity => activity.OperationName == "agentforge.model.invoke"), model =>
            Assert.Contains(executions, execution => execution.TraceId == model.TraceId && execution.SpanId == model.ParentSpanId));
    }

    [Fact]
    public async Task RunAsync_SensitiveInputs_AreExcludedFromNamesTagsAndEvents()
    {
        using var recorder = new ActivityRecorder();
        const string prompt = "prompt-secret-736";
        const string agent = "agent-secret-428";
        const string model = "model-secret-951";
        const string tool = "tool-secret-173";
        const string arguments = "argument-secret-684";
        const string result = "result-secret-205";
        var call = new ToolCall("call-secret-319", tool, Json($"{{\"value\":\"{arguments}\"}}"));
        var client = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(new AssistantMessage(null, [call])),
            ModelResponse.Completed(new AssistantMessage("response-secret-842", [])));
        var request = new AgentRunRequest(
            new AgentDefinition(new AgentId(agent), "instruction-secret-567", new ModelId(model), [tool], new()),
            [new UserMessage(prompt)]);

        await Runtime(client, new DelegateTool(tool, _ => Task.FromResult(new ToolResult(Json($"{{\"value\":\"{result}\"}}")))))
            .RunAsync(request);

        var serialized = recorder.Serialized();
        foreach (var secret in new[] { prompt, agent, model, tool, arguments, result, "response-secret-842", "call-secret-319" })
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.All(recorder.Activities, activity => Assert.Empty(activity.Events));
    }

    [Fact]
    public async Task RunAsync_UsageAndFinishReason_AreRecordedOnModelTurn()
    {
        using var recorder = new ActivityRecorder();
        var client = ScriptedAgentModelClient.FromResponses(
            ModelResponse.Completed(new AssistantMessage("done", []), new ModelUsage(4, 2)));

        await Runtime(client).RunAsync(Request());

        var model = recorder.Single("agentforge.model.invoke");
        Assert.Equal("completed", model.Tag("agentforge.finish.reason"));
        Assert.Equal("4", model.Tag("agentforge.usage.input_tokens"));
        Assert.Equal("2", model.Tag("agentforge.usage.output_tokens"));
    }

    private static ScriptedAgentModelClient CompletedClient() =>
        ScriptedAgentModelClient.FromResponses(ModelResponse.Completed(new AssistantMessage("done", [])));

    private static AgentRuntime Runtime(IAgentModelClient client, params IAgentTool[] tools) =>
        new(client, new AgentToolRegistry(tools));

    private static AgentRuntime Runtime(IAgentModelClient client, TimeProvider timeProvider, params IAgentTool[] tools) =>
        new(client, new AgentToolRegistry(tools), timeProvider);

    private static AgentRunRequest Request(
        IReadOnlyList<string>? tools = null,
        AgentExecutionOptions? options = null) =>
        new(new AgentDefinition(new AgentId("agent"), "instructions", new ModelId("model"), tools ?? [], new(), options ?? AgentExecutionOptions.None),
            [new UserMessage("hello")]);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void AssertFailure(Activity activity, string outcome, string code)
    {
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(outcome, activity.Tag("agentforge.outcome"));
        Assert.Equal(code, activity.Tag("agentforge.error.code"));
    }

    private static async IAsyncEnumerable<ModelStreamEvent> FailingStream(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelTextDelta("partial");
        await Task.Yield();
        throw new ModelProviderException(ModelProviderErrorKind.Transport, true, "stream-secret");
    }

    private static async IAsyncEnumerable<ModelStreamEvent> NeverEndingStream(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ModelTextDelta("partial");
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class DelegateTool(string name, Func<CancellationToken, Task<ToolResult>> invoke) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(name, "description", Json("{\"type\":\"object\"}"));

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            invoke(cancellationToken);
    }

    private sealed class ActivityRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<Activity> _activities = new();

        public ActivityRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity => _activities.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Activities => _activities.ToArray();

        public Activity Single(string operationName) => Assert.Single(Activities, activity => activity.OperationName == operationName);

        public string Serialized() => string.Join('|', Activities.Select(activity =>
            $"{activity.OperationName}:{string.Join(',', activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))}"));

        public void Dispose() => _listener.Dispose();
    }
}

[CollectionDefinition("Activity instrumentation", DisableParallelization = true)]
public sealed class ActivityInstrumentationCollection;

internal static class ActivityTestExtensions
{
    public static string? Tag(this Activity activity, string name) =>
        activity.TagObjects.FirstOrDefault(tag => tag.Key == name).Value?.ToString();
}
