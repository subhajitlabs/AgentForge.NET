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

    private sealed class FinalModelClient : IAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("hosted")));
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
