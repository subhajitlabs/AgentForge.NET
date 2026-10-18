using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Core.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task AddAgentForge_RegisteredModelAndTool_ResolvesWorkingRuntime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddAgentTool<NoOpTool>();
        services.AddAgentForge();
        await using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<IAgentRuntime>();
        var result = await runtime.RunAsync(new AgentRunRequest(
            new AgentDefinition(
                new AgentId("hosted"),
                "Respond deterministically.",
                new ModelId("fake"),
                new HashSet<string>(StringComparer.Ordinal) { "noop" },
                new AgentExecutionLimits()),
            [new UserMessage("hello")]));

        Assert.Equal("hosted", result.FinalMessage.Text);
    }

    [Fact]
    public async Task AddAgentForge_StreamingModel_ResolvesSameStreamingRuntime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, StreamingModelClient>();
        services.AddAgentForge();
        await using var provider = services.BuildServiceProvider();

        var standard = provider.GetRequiredService<IAgentRuntime>();
        var streaming = provider.GetRequiredService<IStreamingAgentRuntime>();
        var events = new List<AgentRunEvent>();
        await foreach (var item in streaming.StreamAsync(new AgentRunRequest(
            new AgentDefinition(
                new AgentId("hosted"),
                "Respond deterministically.",
                new ModelId("fake"),
                [],
                new AgentExecutionLimits()),
            [new UserMessage("hello")])))
        {
            events.Add(item);
        }

        Assert.Same(standard, streaming);
        Assert.Collection(events,
            item => Assert.Equal("host", Assert.IsType<AssistantTextDelta>(item).Text),
            item => Assert.Equal("hosted", Assert.IsType<AgentRunCompleted>(item).Result.FinalMessage.Text));
    }

    private sealed class FinalModelClient : IAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("hosted")));
        }
    }

    private sealed class StreamingModelClient : IAgentModelClient, IStreamingAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("hosted")));

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelTextDelta("host");
            await Task.Yield();
            yield return new ModelResponseCompleted(ModelResponse.Completed(AssistantMessage.FromText("hosted")));
        }
    }

    private sealed class NoOpTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "noop",
            "Returns null.",
            JsonSerializer.SerializeToElement(new { type = "object" }));

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ToolResult(JsonSerializer.SerializeToElement<object?>(null)));
        }
    }
}
