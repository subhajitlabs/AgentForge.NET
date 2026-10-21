using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Core.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task AddAgentDefinition_TokenBudget_IsBoundToKeyedAgent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("budgeted", agent => agent
            .WithInstructions("test").UseModel("fake").WithTokenBudget(10, 20, 30));
        await using var provider = services.BuildServiceProvider();

        var budget = provider.GetRequiredKeyedService<IAgent>("budgeted").Definition.Options.TokenBudget;

        Assert.NotNull(budget);
        Assert.Equal(10, budget.MaxInputTokens);
        Assert.Equal(20, budget.MaxOutputTokens);
        Assert.Equal(30, budget.MaxTotalTokens);
    }

    [Fact]
    public async Task AddAgentDefinition_BufferedRun_UsesBoundImmutableDefinition()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("support", agent => agent
            .WithInstructions("Respond deterministically.").UseModel("fake").WithLimits(2, 1));
        await using var provider = services.BuildServiceProvider();

        var agent = provider.GetRequiredKeyedService<IAgent>("support");
        var first = await agent.RunAsync("first");
        var second = await agent.RunAsync("second");

        Assert.Equal("support", agent.Definition.Id.Value);
        Assert.NotEqual(first.ExecutionId, second.ExecutionId);
        Assert.Equal("hosted", first.FinalMessage.Text);
    }

    [Fact]
    public async Task AddAgentDefinition_StreamingRun_PreservesEvents()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, StreamingModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("stream", agent => agent
            .WithInstructions("Respond deterministically.").UseModel("fake"));
        await using var provider = services.BuildServiceProvider();

        var events = new List<AgentRunEvent>();
        await foreach (var item in provider.GetRequiredKeyedService<IAgent>("stream").StreamAsync("hello"))
            events.Add(item);

        Assert.Collection(events,
            item => Assert.Equal("host", Assert.IsType<AssistantTextDelta>(item).Text),
            item => Assert.Equal("hosted", Assert.IsType<AgentRunCompleted>(item).Result.FinalMessage.Text));
    }

    [Fact]
    public void AddAgentDefinition_DuplicateKey_ThrowsDeterministically()
    {
        var services = new ServiceCollection();
        services.AddAgentDefinition("same", agent => agent.WithInstructions("one").UseModel("fake"));

        Assert.Throws<InvalidOperationException>(() => services.AddAgentDefinition(
            "same", agent => agent.WithInstructions("two").UseModel("fake")));
    }

    [Fact]
    public async Task AddAgentDefinition_MissingKey_HasNoFallback()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("known", agent => agent.WithInstructions("test").UseModel("fake"));
        await using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetKeyedService<IAgent>("missing"));
        Assert.Null(provider.GetService<IAgent>());
    }

    [Fact]
    public async Task AddAgentDefinition_UnknownAllowedTool_FailsBeforeInvocation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("unsafe", agent => agent
            .WithInstructions("test").UseModel("fake").AllowTool("missing"));
        await using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeyedService<IAgent>("unsafe"));
    }

    [Fact]
    public async Task AddAgentForge_MultipleModelClients_FailsBeforeInvocation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("agent", agent => agent.WithInstructions("test").UseModel("fake"));
        await using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeyedService<IAgent>("agent"));
    }

    [Fact]
    public async Task AddAgentDefinition_NonStreamingClient_StreamFailsWithCapabilityCode()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("agent", agent => agent.WithInstructions("test").UseModel("fake"));
        await using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(async () =>
        {
            await foreach (var _ in provider.GetRequiredKeyedService<IAgent>("agent").StreamAsync("hello")) { }
        });

        Assert.Equal(AgentExecutionErrorCode.CapabilityNotSupported, exception.ErrorCode);
    }

    [Fact]
    public async Task AddAgentDefinition_CancellationToken_IsPropagated()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("agent", agent => agent.WithInstructions("test").UseModel("fake"));
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredKeyedService<IAgent>("agent").RunAsync("hello", cancellation.Token));
    }

    [Fact]
    public async Task AddAgentDefinition_TwoKeys_KeepDefinitionsIsolated()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient, FinalModelClient>();
        services.AddAgentForge();
        services.AddAgentDefinition("first", agent => agent.WithInstructions("first instructions").UseModel("one"));
        services.AddAgentDefinition("second", agent => agent.WithInstructions("second instructions").UseModel("two"));
        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredKeyedService<IAgent>("first");
        var second = provider.GetRequiredKeyedService<IAgent>("second");

        Assert.Equal("one", first.Definition.Model.Value);
        Assert.Equal("two", second.Definition.Model.Value);
        Assert.NotSame(first, second);
    }

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
