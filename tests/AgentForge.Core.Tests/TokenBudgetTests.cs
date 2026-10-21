using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;

namespace AgentForge.Core.Tests;

public sealed class TokenBudgetTests
{
    [Fact]
    public async Task RunAsync_BudgetDisabledAndUsageMissing_Completes()
    {
        var result = await Runtime(ModelResponse.Completed(AssistantMessage.FromText("done"))).RunAsync(Request());

        Assert.Null(result.Usage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveBudget_Throws(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTokenBudget(maxInputTokens: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTokenBudget(maxOutputTokens: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTokenBudget(maxTotalTokens: value));
    }

    [Theory]
    [InlineData(2, 3, 5, 2, 3, 5, true)]
    [InlineData(3, 3, 6, 2, 3, 5, true)]
    [InlineData(1, 3, 5, 2, 3, 5, false)]
    [InlineData(2, 2, 5, 2, 3, 5, false)]
    [InlineData(2, 3, 4, 2, 3, 5, false)]
    public async Task RunAsync_SeparateBudgets_EnforceObservedUsage(
        long maxInput, long maxOutput, long maxTotal,
        long input, long output, long expectedTotal, bool accepted)
    {
        var runtime = Runtime(ModelResponse.Completed(
            AssistantMessage.FromText("done"), new ModelUsage(input, output)));
        var task = runtime.RunAsync(Request(new(maxInput, maxOutput, maxTotal)));

        if (accepted)
            Assert.Equal(expectedTotal, (await task).Usage?.TotalTokens);
        else
            Assert.Equal(AgentExecutionErrorCode.TokenBudgetExceeded,
                (await Assert.ThrowsAsync<AgentExecutionException>(() => task)).ErrorCode);
    }

    [Fact]
    public async Task RunAsync_MultipleTurns_UsesCumulativeUsageAndStopsBeforeSecondTool()
    {
        var tool = new RecordingTool();
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("one")), new ModelUsage(2, 1)),
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("two")), new ModelUsage(2, 1)),
            ModelResponse.Completed(AssistantMessage.FromText("must not run"), new ModelUsage(1, 1)));
        var runtime = new AgentRuntime(model, new AgentToolRegistry([tool]));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            runtime.RunAsync(Request(new(maxTotalTokens: 5), ["echo"])));

        Assert.Equal(AgentExecutionErrorCode.TokenBudgetExceeded, exception.ErrorCode);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_MissingUsageWithBudget_FailsClosed()
    {
        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            Runtime(ModelResponse.Completed(AssistantMessage.FromText("done")))
                .RunAsync(Request(new(maxTotalTokens: 10))));

        Assert.Equal(AgentExecutionErrorCode.TokenUsageUnavailableOrInvalid, exception.ErrorCode);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(long.MaxValue, 1)]
    public async Task RunAsync_InvalidOrOverflowingUsage_IsStableFailure(long input, long output)
    {
        var response = ModelResponse.Completed(AssistantMessage.FromText("done"), new ModelUsage(input, output));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(response).RunAsync(Request()));

        Assert.Equal(AgentExecutionErrorCode.TokenUsageUnavailableOrInvalid, exception.ErrorCode);
    }

    [Fact]
    public async Task StreamAsync_TerminalUsage_IsCountedOnce()
    {
        var client = ScriptedStreamingAgentModelClient.FromEvents([
            new ModelTextDelta("done"),
            new ModelResponseCompleted(ModelResponse.Completed(
                AssistantMessage.FromText("done"), new ModelUsage(2, 3)))
        ]);
        var events = new List<AgentRunEvent>();

        await foreach (var item in new AgentRuntime(client, new AgentToolRegistry([]))
            .StreamAsync(Request(new(maxTotalTokens: 5))))
            events.Add(item);

        Assert.Equal(5, Assert.IsType<AgentRunCompleted>(events[^1]).Result.Usage?.TotalTokens);
    }

    [Fact]
    public async Task StreamAsync_OverBudget_EmitsNoCompletion()
    {
        var client = ScriptedStreamingAgentModelClient.FromEvents([
            new ModelTextDelta("provisional"),
            new ModelResponseCompleted(ModelResponse.Completed(
                AssistantMessage.FromText("provisional"), new ModelUsage(3, 3)))
        ]);
        var observed = new List<AgentRunEvent>();

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(async () =>
        {
            await foreach (var item in new AgentRuntime(client, new AgentToolRegistry([]))
                .StreamAsync(Request(new(maxTotalTokens: 5))))
                observed.Add(item);
        });

        Assert.Equal(AgentExecutionErrorCode.TokenBudgetExceeded, exception.ErrorCode);
        Assert.DoesNotContain(observed, item => item is AgentRunCompleted);
    }

    private static AgentRuntime Runtime(params ModelResponse[] responses) =>
        new(ScriptedAgentModelClient.FromResponses(responses), new AgentToolRegistry([]));

    private static AgentRunRequest Request(AgentTokenBudget? budget = null, IEnumerable<string>? tools = null) => new(
        new AgentDefinition(new AgentId("tokens"), "instructions", new ModelId("model"), tools ?? [],
            new AgentExecutionLimits(), budget is null ? AgentExecutionOptions.None : new AgentExecutionOptions(budget)),
        [new UserMessage("hello")]);

    private static ToolCall Call(string id) => new(id, "echo", JsonDocument.Parse("{}").RootElement.Clone());

    private sealed class RecordingTool : IAgentTool
    {
        private int _invocationCount;
        public ToolDescriptor Descriptor { get; } = new("echo", "echo", JsonDocument.Parse("{}").RootElement.Clone());
        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return Task.FromResult(new ToolResult(JsonSerializer.SerializeToElement(new { ok = true })));
        }
    }
}
