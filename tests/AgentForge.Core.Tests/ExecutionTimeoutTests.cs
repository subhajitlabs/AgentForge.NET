using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;
using AgentForge.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Core.Tests;

public sealed class ExecutionTimeoutTests
{
    private static readonly TimeSpan TimeoutDuration = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task RunAsync_NoTimeout_PreservesDefaultBehavior()
    {
        var time = new ManualTimeProvider();
        var runtime = Runtime(new DelegateClient((_, _) => Task.FromResult(Completed())), time);

        time.Advance(TimeSpan.FromDays(1));
        var result = await runtime.RunAsync(Request());

        Assert.Equal("done", result.FinalMessage.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AgentExecutionOptions_NonPositiveTimeout_Throws(int ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentExecutionOptions(TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public async Task RunAsync_BufferedProviderTimeout_ThrowsStableCode()
    {
        var entered = Signal();
        var time = new ManualTimeProvider();
        var runtime = Runtime(new DelegateClient(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Completed();
        }), time);

        var run = runtime.RunAsync(Request(TimeoutDuration));
        await entered.Task;
        time.Advance(TimeoutDuration);

        await AssertTimedOutAsync(run);
    }

    [Fact]
    public async Task StreamAsync_EnumerationTimeout_EmitsNoCompletion()
    {
        var entered = Signal();
        var time = new ManualTimeProvider();
        var observed = new List<AgentRunEvent>();
        var runtime = Runtime(new DelegateClient(
            (_, _) => Task.FromResult(Completed()),
            (_, token) => StreamingWaitAsync(entered, token)), time);

        var run = CollectAsync(runtime.StreamAsync(Request(TimeoutDuration)), observed);
        await entered.Task;
        time.Advance(TimeoutDuration);

        await AssertTimedOutAsync(run);
        Assert.DoesNotContain(observed, item => item is AgentRunCompleted);
    }

    [Fact]
    public async Task RunAsync_ToolTimeout_ThrowsStableCode()
    {
        var entered = Signal();
        var time = new ManualTimeProvider();
        var tool = new DelegateTool(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var model = new QueueClient(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call())),
            Completed());
        var runtime = Runtime(model, time, tool);

        var run = runtime.RunAsync(Request(TimeoutDuration, ["work"]));
        await entered.Task;
        time.Advance(TimeoutDuration);

        await AssertTimedOutAsync(run);
    }

    [Fact]
    public async Task RunAsync_PreCancelledCaller_ThrowsCancellationWithoutProviderCall()
    {
        var calls = 0;
        var runtime = Runtime(new DelegateClient((_, _) =>
        {
            calls++;
            return Task.FromResult(Completed());
        }), new ManualTimeProvider());
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.RunAsync(Request(TimeoutDuration), caller.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunAsync_CallerCancellationDuringExecution_ThrowsCancellation()
    {
        var entered = Signal();
        var runtime = Runtime(new DelegateClient(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Completed();
        }), new ManualTimeProvider());
        using var caller = new CancellationTokenSource();

        var run = runtime.RunAsync(Request(TimeoutDuration), caller.Token);
        await entered.Task;
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_CallerAndDeadlineRace_CallerWins()
    {
        var entered = Signal();
        var release = Signal();
        var time = new ManualTimeProvider();
        var runtime = Runtime(new DelegateClient(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return Completed();
        }), time);
        using var caller = new CancellationTokenSource();

        var run = runtime.RunAsync(Request(TimeoutDuration), caller.Token);
        await entered.Task;
        time.Advance(TimeoutDuration);
        caller.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_ProviderIgnoringCancellation_CannotReturnSuccessAfterDeadline()
    {
        var entered = Signal();
        var release = Signal();
        var time = new ManualTimeProvider();
        var runtime = Runtime(new DelegateClient(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return Completed();
        }), time);

        var run = runtime.RunAsync(Request(TimeoutDuration));
        await entered.Task;
        time.Advance(TimeoutDuration);
        Assert.False(run.IsCompleted);
        release.SetResult();

        await AssertTimedOutAsync(run);
    }

    [Fact]
    public async Task RunAsync_ToolIgnoringCancellation_CannotReturnSuccessAfterDeadline()
    {
        var entered = Signal();
        var release = Signal();
        var time = new ManualTimeProvider();
        var tool = new DelegateTool(async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        var runtime = Runtime(new QueueClient(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call())),
            Completed()), time, tool);

        var run = runtime.RunAsync(Request(TimeoutDuration, ["work"]));
        await entered.Task;
        time.Advance(TimeoutDuration);
        Assert.False(run.IsCompleted);
        release.SetResult();

        await AssertTimedOutAsync(run);
    }

    [Fact]
    public async Task StreamAsync_TimeoutAfterDelta_LeavesDeltaProvisional()
    {
        var emitted = Signal();
        var time = new ManualTimeProvider();
        var observed = new List<AgentRunEvent>();
        var runtime = Runtime(new DelegateClient(
            (_, _) => Task.FromResult(Completed()),
            (_, token) => DeltaThenWaitAsync(emitted, token)), time);

        var run = CollectAsync(runtime.StreamAsync(Request(TimeoutDuration)), observed);
        await emitted.Task;
        await WaitUntilAsync(() => observed.Count == 1);
        time.Advance(TimeoutDuration);

        await AssertTimedOutAsync(run);
        Assert.Single(observed);
        Assert.IsType<AssistantTextDelta>(observed[0]);
    }

    [Fact]
    public async Task AddAgentDefinition_WithTimeout_UsesKeyedConfiguration()
    {
        var entered = Signal();
        var time = new ManualTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IAgentModelClient>(new DelegateClient(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Completed();
        }));
        services.AddAgentForge();
        services.AddAgentDefinition("timed", builder => builder
            .WithInstructions("test")
            .UseModel("fake")
            .WithTimeout(TimeoutDuration));
        await using var provider = services.BuildServiceProvider();

        var run = provider.GetRequiredKeyedService<IAgent>("timed").RunAsync("hello");
        await entered.Task;
        time.Advance(TimeoutDuration);

        await AssertTimedOutAsync(run);
    }

    [Fact]
    public async Task RunAsync_ConcurrentExecutions_KeepDeadlineStateIsolated()
    {
        var blocked = Signal();
        var release = Signal();
        var time = new ManualTimeProvider();
        var client = new DelegateClient(async (request, _) =>
        {
            if (request.Messages.OfType<UserMessage>().Single().Text == "blocked")
            {
                blocked.SetResult();
                await release.Task;
            }
            return Completed();
        });
        var runtime = Runtime(client, time);

        var timedRun = runtime.RunAsync(Request(TimeoutDuration, message: "blocked"));
        await blocked.Task;
        var successful = await runtime.RunAsync(Request(message: "success"));
        time.Advance(TimeoutDuration);
        release.SetResult();

        Assert.Equal("done", successful.FinalMessage.Text);
        await AssertTimedOutAsync(timedRun);
    }

    private static AgentRuntime Runtime(IAgentModelClient client, ManualTimeProvider time, params IAgentTool[] tools) =>
        new(client, new AgentToolRegistry(tools), time);

    private static AgentRunRequest Request(
        TimeSpan? timeout = null,
        IEnumerable<string>? tools = null,
        string message = "hello") => new(
            timeout is null
                ? new AgentDefinition(new AgentId("agent"), "instructions", new ModelId("model"), tools ?? [], new())
                : new AgentDefinition(new AgentId("agent"), "instructions", new ModelId("model"), tools ?? [], new(), new(timeout.Value)),
            [new UserMessage(message)]);

    private static async Task AssertTimedOutAsync(Task task)
    {
        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => task);
        Assert.Equal(AgentExecutionErrorCode.ExecutionTimedOut, exception.ErrorCode);
    }

    private static async Task CollectAsync(IAsyncEnumerable<AgentRunEvent> stream, List<AgentRunEvent> observed)
    {
        await foreach (var item in stream)
            observed.Add(item);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var index = 0; index < 100 && !condition(); index++)
            await Task.Yield();
        Assert.True(condition());
    }

    private static async IAsyncEnumerable<ModelStreamEvent> StreamingWaitAsync(
        TaskCompletionSource entered,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        entered.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> DeltaThenWaitAsync(
        TaskCompletionSource emitted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ModelTextDelta("partial");
        emitted.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static ModelResponse Completed() => ModelResponse.Completed(AssistantMessage.FromText("done"));
    private static ToolCall Call() => new("call", "work", JsonSerializer.SerializeToElement(new { }));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegateClient(
        Func<ModelRequest, CancellationToken, Task<ModelResponse>> complete,
        Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>>? stream = null)
        : IAgentModelClient, IStreamingAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            complete(request, cancellationToken);

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            stream?.Invoke(request, cancellationToken)
            ?? throw new NotSupportedException();
    }

    private sealed class QueueClient(params ModelResponse[] responses) : IAgentModelClient
    {
        private readonly Queue<ModelResponse> _responses = new(responses);

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_responses.Dequeue());
    }

    private sealed class DelegateTool(Func<CancellationToken, Task> invoke) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "work", "test", JsonSerializer.SerializeToElement(new { type = "object" }));

        public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            await invoke(cancellationToken);
            return new ToolResult(JsonSerializer.SerializeToElement<object?>(null));
        }
    }
}
