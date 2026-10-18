using System.Collections.Concurrent;
using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;

namespace AgentForge.Core.Tests;

public sealed class AgentRuntimeTests
{
    [Fact]
    public async Task RunAsync_FinalResponse_CompletesWithTranscriptAndUsage()
    {
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.Completed(AssistantMessage.FromText("done"), new ModelUsage(3, 2)));

        var result = await CreateRuntime(model).RunAsync(Request());

        Assert.Equal("done", result.FinalMessage.Text);
        Assert.Collection(result.Transcript,
            message => Assert.IsType<SystemMessage>(message),
            message => Assert.IsType<UserMessage>(message),
            message => Assert.IsType<AssistantMessage>(message));
        Assert.Equal(1, result.ModelCalls);
        Assert.Equal(0, result.ToolCalls);
        Assert.Equal(5, result.Usage?.TotalTokens);
    }

    [Fact]
    public async Task RunAsync_ToolRoundTrip_PreservesCallAndResultCorrelation()
    {
        var tool = new RecordingTool("echo");
        var model = new ScriptedAgentModelClient([
            (_, _) => Task.FromResult(ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("id-1", "echo")))),
            (request, _) =>
            {
                var assistant = Assert.IsType<AssistantMessage>(request.Messages[^2]);
                var toolResult = Assert.IsType<ToolResultMessage>(request.Messages[^1]);
                Assert.Equal("id-1", assistant.ToolCalls.Single().Id);
                Assert.Equal("id-1", toolResult.ToolCallId);
                return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("complete")));
            }
        ]);

        var result = await CreateRuntime(model, tool).RunAsync(Request(Tools("echo")));

        Assert.Equal(2, result.ModelCalls);
        Assert.Equal(1, result.ToolCalls);
        Assert.Single(tool.Invocations);
    }

    [Fact]
    public async Task RunAsync_MultipleTools_ExecutesInModelOrder()
    {
        var order = new ConcurrentQueue<string>();
        var first = new RecordingTool("first", invocation => order.Enqueue("first"));
        var second = new RecordingTool("second", invocation => order.Enqueue("second"));
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("1", "first"), Call("2", "second"))),
            ModelResponse.Completed(AssistantMessage.FromText("done")));

        await CreateRuntime(model, first, second).RunAsync(Request(Tools("first", "second")));

        Assert.Equal(["first", "second"], order);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public async Task RunAsync_NonPositiveLimits_RejectsBeforeModelCall(int maxModels, int maxTools)
    {
        var invoked = false;
        var model = new ScriptedAgentModelClient([(_, _) =>
        {
            invoked = true;
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("unused")));
        }]);

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() =>
            CreateRuntime(model).RunAsync(Request(limits: new(maxModels, maxTools))));

        Assert.False(invoked);
    }

    [Fact]
    public async Task RunAsync_ModelCallLimit_RejectsBeforeExtraInvocation()
    {
        var calls = 0;
        var model = new ScriptedAgentModelClient([
            (_, _) => { calls++; return Task.FromResult(ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("1", "echo")))); },
            (_, _) => { calls++; return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("must not run"))); }
        ]);

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CreateRuntime(model, new RecordingTool("echo")).RunAsync(
                Request(Tools("echo"), new AgentExecutionLimits(1, 2))));

        Assert.Equal(AgentExecutionErrorCode.ModelCallLimitExceeded, exception.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_OverBudgetBatch_ExecutesNoTools()
    {
        var tool = new RecordingTool("echo");
        var model = ScriptedAgentModelClient.FromResponses(ModelResponse.ToolCalls(
            AssistantMessage.FromToolCalls(Call("1", "echo"), Call("2", "echo"))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CreateRuntime(model, tool).RunAsync(Request(Tools("echo"), new AgentExecutionLimits(2, 1))));

        Assert.Equal(AgentExecutionErrorCode.ToolCallLimitExceeded, exception.ErrorCode);
        Assert.Empty(tool.Invocations);
    }

    [Theory]
    [InlineData("duplicate", AgentExecutionErrorCode.InvalidModelResponse)]
    [InlineData("unauthorized", AgentExecutionErrorCode.ToolNotAllowed)]
    [InlineData("invalid-json", AgentExecutionErrorCode.InvalidModelResponse)]
    public async Task RunAsync_InvalidToolBatch_ExecutesNoTools(
        string scenario,
        AgentExecutionErrorCode expectedCode)
    {
        var valid = new RecordingTool("valid");
        ToolCall bad = scenario switch
        {
            "duplicate" => Call("same", "valid"),
            "unauthorized" => Call("bad", "secret"),
            _ => new ToolCall("bad", "valid", default)
        };
        var first = scenario == "duplicate" ? Call("same", "valid") : Call("first", "valid");
        IReadOnlySet<string> allowed = new HashSet<string>(["valid"], StringComparer.Ordinal);
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(first, bad)));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            CreateRuntime(model, valid).RunAsync(Request(allowed)));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Empty(valid.Invocations);
    }

    [Fact]
    public async Task RunAsync_AlreadyCancelled_DoesNotCallProvider()
    {
        var invoked = false;
        var model = new ScriptedAgentModelClient([(_, _) =>
        {
            invoked = true;
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("unused")));
        }]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateRuntime(model).RunAsync(Request(), cancellation.Token));
        Assert.False(invoked);
    }

    [Fact]
    public async Task RunAsync_CancelDuringProvider_PropagatesCancellation()
    {
        var entered = NewSignal();
        var model = new ScriptedAgentModelClient([async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }]);
        using var cancellation = new CancellationTokenSource();
        var run = CreateRuntime(model).RunAsync(Request(), cancellation.Token);
        await entered.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_CancelDuringTool_PropagatesCancellation()
    {
        var entered = NewSignal();
        var tool = new RecordingTool("echo", async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("null");
        });
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("1", "echo"))));
        using var cancellation = new CancellationTokenSource();
        var run = CreateRuntime(model, tool).RunAsync(Request(Tools("echo")), cancellation.Token);
        await entered.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_ProviderFailure_PreservesCause()
    {
        var cause = new InvalidOperationException("provider failed");
        var model = new ScriptedAgentModelClient([(_, _) => Task.FromException<ModelResponse>(cause)]);

        var exception = await Assert.ThrowsAsync<AgentProviderException>(() => CreateRuntime(model).RunAsync(Request()));

        Assert.Same(cause, exception.InnerException);
        Assert.NotEqual(Guid.Empty, exception.ExecutionId.Value);
    }

    [Fact]
    public async Task RunAsync_ToolFailure_PreservesCauseAndContext()
    {
        var cause = new InvalidOperationException("tool failed");
        var tool = new RecordingTool("echo", (_, _) => Task.FromException<JsonElement>(cause));
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("call-x", "echo"))));

        var exception = await Assert.ThrowsAsync<AgentToolException>(() =>
            CreateRuntime(model, tool).RunAsync(Request(Tools("echo"))));

        Assert.Same(cause, exception.InnerException);
        Assert.Equal("echo", exception.ToolName);
        Assert.Equal("call-x", exception.ToolCallId);
    }

    [Fact]
    public async Task RunAsync_NegativeUsage_IsInvalidModelResponse()
    {
        var model = ScriptedAgentModelClient.FromResponses(ModelResponse.Completed(
            AssistantMessage.FromText("text"),
            new ModelUsage(-1, 0)));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => CreateRuntime(model).RunAsync(Request()));

        Assert.Equal(AgentExecutionErrorCode.InvalidModelResponse, exception.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_ConcurrentRuns_DoNotShareState()
    {
        var runtime = CreateRuntime(new EchoModelClient());

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(index => runtime.RunAsync(Request(message: $"message-{index}"))));

        Assert.Equal(20, results.Select(result => result.ExecutionId).Distinct().Count());
        Assert.All(results, result => Assert.Equal(3, result.Transcript.Count));
        Assert.Equal(
            Enumerable.Range(0, 20).Select(index => $"message-{index}").Order(),
            results.Select(result => result.FinalMessage.Text).Order());
    }

    private static AgentRuntime CreateRuntime(IAgentModelClient model, params IAgentTool[] tools) =>
        new(model, new AgentToolRegistry(tools));

    private static AgentRunRequest Request(
        IReadOnlySet<string>? allowedTools = null,
        AgentExecutionLimits? limits = null,
        string message = "hello") => new(
            new AgentDefinition(
                new AgentId("test-agent"),
                "Test instructions",
                new ModelId("test-model"),
                allowedTools ?? new HashSet<string>(StringComparer.Ordinal),
                limits ?? new AgentExecutionLimits()),
            [new UserMessage(message)]);

    private static ToolCall Call(string id, string name) => new(id, name, Json("{}"));

    private static IReadOnlySet<string> Tools(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingTool : IAgentTool
    {
        private readonly Func<ToolInvocation, CancellationToken, Task<JsonElement>> _invoke;

        public RecordingTool(string name, Action<ToolInvocation>? onInvoke = null)
            : this(name, (invocation, _) =>
            {
                onInvoke?.Invoke(invocation);
                return Task.FromResult(Json("{\"ok\":true}"));
            }) { }

        public RecordingTool(string name, Func<ToolInvocation, CancellationToken, Task<JsonElement>> invoke)
        {
            Descriptor = new ToolDescriptor(name, $"Test tool {name}", Json("{\"type\":\"object\"}"));
            _invoke = invoke;
        }

        public ToolDescriptor Descriptor { get; }
        public ConcurrentQueue<ToolInvocation> Invocations { get; } = new();

        public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            Invocations.Enqueue(invocation);
            return new ToolResult(await _invoke(invocation, cancellationToken));
        }
    }

    private sealed class EchoModelClient : IAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = request.Messages.OfType<UserMessage>().Single().Text;
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText(text)));
        }
    }
}
