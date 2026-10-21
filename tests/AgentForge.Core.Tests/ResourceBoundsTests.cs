using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;
using AgentForge.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Core.Tests;

public sealed class ResourceBoundsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AgentResourceLimits_NonPositiveValue_Throws(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentResourceLimits(maxInputCharacters: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentResourceLimits(maxOutputCharacters: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentResourceLimits(maxToolArgumentBytes: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentResourceLimits(maxToolResultBytes: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentResourceLimits(maxProviderStreamEvents: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentResourceLimits(maxProviderContentCharacters: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentResourceLimits(maxProviderToolArgumentBytes: value));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(2, false)]
    public async Task StreamAsync_ProviderEventBoundary_IsInclusive(long maximum, bool accepted)
    {
        var observed = new List<AgentRunEvent>();
        var stream = Runtime(new StreamingClient(
            new ModelTextDelta("a"), new ModelTextDelta("b"), Completed("ab")))
            .StreamAsync(Request(resources: new(maxProviderStreamEvents: maximum)));

        if (accepted)
        {
            await foreach (var item in stream) observed.Add(item);
            Assert.Contains(observed, item => item is AgentRunCompleted);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<AgentExecutionException>(async () =>
            {
                await foreach (var item in stream) observed.Add(item);
            });
            Assert.Equal(AgentExecutionErrorCode.ProviderProcessingLimitExceeded, exception.ErrorCode);
            Assert.Equal(["a", "b"], observed.OfType<AssistantTextDelta>().Select(item => item.Text));
            Assert.DoesNotContain(observed, item => item is AgentRunCompleted);
        }
    }

    [Fact]
    public async Task StreamAsync_ProviderProcessingFailure_IsSanitizedAndNotRetried()
    {
        const string secret = "provider-processing-secret-9812";
        var calls = 0;
        var model = new ThrowingStreamingClient(() =>
        {
            calls++;
            return new ModelProviderException(
                ModelProviderErrorKind.ProviderProcessingLimitExceeded,
                false,
                "Provider processing exceeded a configured resource bound.",
                new InvalidOperationException(secret));
        });

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(async () =>
        {
            await foreach (var _ in Runtime(model).StreamAsync(Request())) { }
        });

        Assert.Equal(AgentExecutionErrorCode.ProviderProcessingLimitExceeded, exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_DisabledLimits_PreserveExistingBehavior()
    {
        var output = new string('x', 10_000);
        var result = await Runtime(ScriptedAgentModelClient.FromResponses(
            ModelResponse.Completed(AssistantMessage.FromText(output)))).RunAsync(Request());

        Assert.Equal(output, result.FinalMessage.Text);
    }

    [Theory]
    [InlineData(17, true)]
    [InlineData(16, false)]
    public async Task RunAsync_InputBoundary_IsInclusive(long maximum, bool accepted)
    {
        var calls = 0;
        var model = new ScriptedAgentModelClient([(_, _) =>
        {
            calls++;
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("ok")));
        }]);
        var run = Runtime(model).RunAsync(Request(resources: new(maxInputCharacters: maximum)));

        if (accepted)
            Assert.Equal("ok", (await run).FinalMessage.Text);
        else
            Assert.Equal(AgentExecutionErrorCode.InputSizeLimitExceeded,
                (await Assert.ThrowsAsync<AgentExecutionException>(() => run)).ErrorCode);
        Assert.Equal(accepted ? 1 : 0, calls);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(3, false)]
    public async Task RunAsync_BufferedOutputBoundary_IsInclusive(long maximum, bool accepted)
    {
        var run = Runtime(ScriptedAgentModelClient.FromResponses(
            ModelResponse.Completed(AssistantMessage.FromText("done"))))
            .RunAsync(Request(resources: new(maxOutputCharacters: maximum)));

        if (accepted)
            Assert.Equal("done", (await run).FinalMessage.Text);
        else
            Assert.Equal(AgentExecutionErrorCode.OutputSizeLimitExceeded,
                (await Assert.ThrowsAsync<AgentExecutionException>(() => run)).ErrorCode);
    }

    [Fact]
    public async Task StreamAsync_OutputOverflowAcrossChunks_EmitsOnlyAcceptedDeltasAndNoCompletion()
    {
        var observed = new List<AgentRunEvent>();
        var stream = Runtime(new StreamingClient(
            new ModelTextDelta("ab"),
            new ModelTextDelta("cd"),
            new ModelTextDelta("e"),
            Completed("abcde"))).StreamAsync(Request(resources: new(maxOutputCharacters: 4)));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(async () =>
        {
            await foreach (var item in stream)
                observed.Add(item);
        });

        Assert.Equal(AgentExecutionErrorCode.OutputSizeLimitExceeded, exception.ErrorCode);
        Assert.Equal(["ab", "cd"], observed.OfType<AssistantTextDelta>().Select(item => item.Text));
        Assert.DoesNotContain(observed, item => item is AgentRunCompleted);
    }

    [Fact]
    public async Task StreamAsync_TerminalOutputOverflowAfterAcceptedDelta_ProducesNoCompletion()
    {
        var observed = new List<AgentRunEvent>();
        var stream = Runtime(new StreamingClient(new ModelTextDelta("ok"), Completed("oversized")))
            .StreamAsync(Request(resources: new(maxOutputCharacters: 2)));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(async () =>
        {
            await foreach (var item in stream)
                observed.Add(item);
        });

        Assert.Equal(AgentExecutionErrorCode.OutputSizeLimitExceeded, exception.ErrorCode);
        Assert.Single(observed, item => item is AssistantTextDelta);
        Assert.DoesNotContain(observed, item => item is AgentRunCompleted);
    }

    [Fact]
    public async Task RunAsync_OversizedArgumentsInBatch_ExecutesNoTools()
    {
        var tool = new CountingTool(Json("null"));
        var model = ScriptedAgentModelClient.FromResponses(ModelResponse.ToolCalls(
            AssistantMessage.FromToolCalls(
                new ToolCall("one", "tool", Json("{}")),
                new ToolCall("two", "tool", Json("{}")))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(model, tool).RunAsync(
            Request(["tool"], new(maxToolArgumentBytes: 3))));

        Assert.Equal(AgentExecutionErrorCode.ToolArgumentSizeLimitExceeded, exception.ErrorCode);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_MultibyteArgument_UsesUtf8BytesAndAcceptsEquality()
    {
        var tool = new CountingTool(Json("null"));
        var argument = Json("\"é\"");
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(new ToolCall("one", "tool", argument))),
            ModelResponse.Completed(AssistantMessage.FromText("ok")));

        var result = await Runtime(model, tool).RunAsync(Request(["tool"], new(maxToolArgumentBytes: 4)));

        Assert.Equal("ok", result.FinalMessage.Text);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_OversizedToolResult_IsNotReturnedToModel()
    {
        var providerCalls = 0;
        var tool = new CountingTool(Json("\"é\""));
        var model = new ScriptedAgentModelClient([
            (_, _) =>
            {
                providerCalls++;
                return Task.FromResult(ModelResponse.ToolCalls(
                    AssistantMessage.FromToolCalls(new ToolCall("one", "tool", Json("{}")))));
            },
            (_, _) =>
            {
                providerCalls++;
                return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("unused")));
            }
        ]);

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(model, tool).RunAsync(
            Request(["tool"], new(maxToolResultBytes: 3))));

        Assert.Equal(AgentExecutionErrorCode.ToolResultSizeLimitExceeded, exception.ErrorCode);
        Assert.Equal(1, tool.InvocationCount);
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task RunAsync_ArgumentsAccumulateAcrossBatches()
    {
        var tool = new CountingTool(Json("null"));
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(new ToolCall("one", "tool", Json("{}")))),
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(new ToolCall("two", "tool", Json("{}")))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(model, tool).RunAsync(
            Request(["tool"], new(maxToolArgumentBytes: 3))));

        Assert.Equal(AgentExecutionErrorCode.ToolArgumentSizeLimitExceeded, exception.ErrorCode);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_ResultsAccumulateAcrossCalls()
    {
        var tool = new CountingTool(Json("null"));
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(new ToolCall("one", "tool", Json("{}")))),
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(new ToolCall("two", "tool", Json("{}")))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() => Runtime(model, tool).RunAsync(
            Request(["tool"], new(maxToolResultBytes: 7))));

        Assert.Equal(AgentExecutionErrorCode.ToolResultSizeLimitExceeded, exception.ErrorCode);
        Assert.Equal(2, tool.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_PrecedesInputLimit()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runtime(ScriptedAgentModelClient.FromResponses()).RunAsync(
                Request(resources: new(maxInputCharacters: 1)), cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_LimitErrorMessage_DoesNotContainRejectedPayload()
    {
        const string secret = "secret-resource-payload-4821";
        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            Runtime(ScriptedAgentModelClient.FromResponses()).RunAsync(
                Request(message: secret, resources: new(maxInputCharacters: 1))));

        Assert.Equal(AgentExecutionErrorCode.InputSizeLimitExceeded, exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAgentDefinition_ResourceLimitsRemainIsolatedByKey()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FixedModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("bounded", builder => builder.WithInstructions("i").UseModel("m")
            .WithResourceLimits(maxOutputCharacters: 1));
        services.AddAgentDefinition("unbounded", builder => builder.WithInstructions("i").UseModel("m"));
        await using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            provider.GetRequiredKeyedService<IAgent>("bounded").RunAsync("x"));
        var result = await provider.GetRequiredKeyedService<IAgent>("unbounded").RunAsync("x");

        Assert.Equal(AgentExecutionErrorCode.OutputSizeLimitExceeded, exception.ErrorCode);
        Assert.Equal("ok", result.FinalMessage.Text);
    }

    [Fact]
    public async Task AddAgentDefinition_ProviderProcessingLimitsRemainIsolatedByKey()
    {
        var model = new RecordingLimitsModelClient();
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient>(model);
        services.AddAgentForge();
        services.AddAgentDefinition("bounded", builder => builder.WithInstructions("i").UseModel("m")
            .WithResourceLimits(maxProviderStreamEvents: 7, maxProviderContentCharacters: 11,
                maxProviderToolArgumentBytes: 13));
        services.AddAgentDefinition("unbounded", builder => builder.WithInstructions("i").UseModel("m"));
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredKeyedService<IAgent>("bounded").RunAsync("x");
        await provider.GetRequiredKeyedService<IAgent>("unbounded").RunAsync("x");

        Assert.Collection(model.Observed,
            limits =>
            {
                Assert.Equal(7, limits?.MaxProviderStreamEvents);
                Assert.Equal(11, limits?.MaxProviderContentCharacters);
                Assert.Equal(13, limits?.MaxProviderToolArgumentBytes);
            },
            Assert.Null);
    }

    private static AgentRuntime Runtime(IAgentModelClient model, params IAgentTool[] tools) =>
        new(model, new AgentToolRegistry(tools));

    private static AgentRunRequest Request(
        IEnumerable<string>? allowedTools = null,
        AgentResourceLimits? resources = null,
        string message = "hello") => new(
            new AgentDefinition(new AgentId("agent"), "instructions", new ModelId("model"), allowedTools ?? [],
                new AgentExecutionLimits(), resources is null ? AgentExecutionOptions.None : new(resources)),
            [new UserMessage(message)]);

    private static ModelResponseCompleted Completed(string text) =>
        new(ModelResponse.Completed(AssistantMessage.FromText(text)));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class CountingTool(JsonElement result) : IAgentTool
    {
        private int _invocationCount;
        public ToolDescriptor Descriptor { get; } = new("tool", "test", Json("{\"type\":\"object\"}"));
        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return Task.FromResult(new ToolResult(result));
        }
    }

    private sealed class StreamingClient(params ModelStreamEvent[] events) : IAgentModelClient, IStreamingAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class FixedModelClient : IAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("ok")));
    }

    private sealed class RecordingLimitsModelClient : IAgentModelClient
    {
        public List<AgentResourceLimits?> Observed { get; } = [];

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Observed.Add(request.ResourceLimits);
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("ok")));
        }
    }

    private sealed class ThrowingStreamingClient(Func<Exception> exceptionFactory) : IAgentModelClient, IStreamingAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw exceptionFactory();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
