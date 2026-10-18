using System.Collections.Concurrent;
using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;
using AgentForge.Hosting;
using AgentForge.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tools.Tests;

public sealed class TypedToolRuntimeTests
{
    [Fact]
    public async Task RunAsync_OneInvalidCallInBatch_ExecutesNoTools()
    {
        var invocations = 0;
        var first = Tool("first", () => invocations++);
        var second = Tool("second", () => invocations++);
        var calls = new[]
        {
            new ToolCall("one", "first", Json("{\"Value\":1}")),
            new ToolCall("two", "second", Json("{\"Value\":\"sensitive-invalid-value\"}"))
        };
        var runtime = Runtime(ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(calls))), first, second);

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            runtime.RunAsync(Request(["first", "second"])));

        Assert.Equal(AgentExecutionErrorCode.InvalidToolArguments, exception.ErrorCode);
        Assert.Equal(0, invocations);
        Assert.DoesNotContain("sensitive-invalid-value", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsync_InvalidTypedCall_UsesSameAtomicPrevalidation()
    {
        var invocations = 0;
        var tool = Tool("typed", () => invocations++);
        var response = ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(
            new ToolCall("one", "typed", Json("{}"))));
        var runtime = Runtime(ScriptedStreamingAgentModelClient.FromEvents(
            [new ModelResponseCompleted(response)]), tool);

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(async () =>
        {
            await foreach (var _ in runtime.StreamAsync(Request(["typed"]))) { }
        });

        Assert.Equal(AgentExecutionErrorCode.InvalidToolArguments, exception.ErrorCode);
        Assert.Equal(0, invocations);
    }

    [Fact]
    public async Task RunAsync_UntypedTool_RemainsCompatible()
    {
        var model = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(new ToolCall("one", "legacy", Json("[]")))),
            ModelResponse.Completed(AssistantMessage.FromText("done")));
        var legacy = new LegacyTool();

        var result = await Runtime(model, legacy).RunAsync(Request(["legacy"]));

        Assert.Equal(1, legacy.Invocations);
        Assert.Equal("done", result.FinalMessage.Text);
    }

    [Fact]
    public void AddAgentTool_InstanceRegistration_DoesNotAuthorizeTool()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentModelClient>(ScriptedAgentModelClient.FromResponses(
            ModelResponse.Completed(AssistantMessage.FromText("done"))));
        services.AddAgentTool(Tool("typed", static () => { }));
        services.AddAgentForge();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentToolRegistry>();

        Assert.True(registry.TryResolve("typed", out _));
        Assert.Empty(registry.Describe(new HashSet<string>()));
    }

    [Fact]
    public async Task ConcurrentRuns_DoNotShareArgumentsOrContext()
    {
        var observed = new ConcurrentDictionary<ExecutionId, int>();
        var tool = AgentTool.Create<Arguments, Result>(
            "typed", "description",
            (arguments, context, _) =>
            {
                observed[context.ExecutionId] = arguments.Value;
                return ValueTask.FromResult(new Result(arguments.Value));
            });
        var model = new EchoModelClient();
        var runtime = Runtime(model, tool);

        var runs = await Task.WhenAll(
            runtime.RunAsync(Request(["typed"], "11")),
            runtime.RunAsync(Request(["typed"], "22")));

        Assert.Equal(2, observed.Count);
        Assert.Equal([11, 22], observed.Values.Order().ToArray());
        Assert.NotEqual(runs[0].ExecutionId, runs[1].ExecutionId);
    }

    private static IAgentTool Tool(string name, Action invoked) => AgentTool.Create<Arguments, Result>(
        name, "description",
        (arguments, _, _) =>
        {
            invoked();
            return ValueTask.FromResult(new Result(arguments.Value));
        });

    private static AgentRuntime Runtime(IAgentModelClient client, params IAgentTool[] tools) =>
        new(client, new AgentToolRegistry(tools));

    private static AgentRunRequest Request(string[] tools, string message = "test") => new(
        new AgentDefinition(new AgentId("agent"), "instructions", new ModelId("model"), tools, new AgentExecutionLimits(3, 3)),
        [new UserMessage(message)]);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed record Arguments(int Value);
    private sealed record Result(int Value);

    private sealed class LegacyTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("legacy", "description", Json("{\"type\":\"array\"}"));
        public int Invocations { get; private set; }
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.FromResult(new ToolResult(Json("null")));
        }
    }

    private sealed class EchoModelClient : IAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            var result = request.Messages.OfType<ToolResultMessage>().LastOrDefault();
            if (result is not null)
                return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("done")));
            var value = int.Parse(((UserMessage)request.Messages[1]).Text, System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(
                new ToolCall(Guid.NewGuid().ToString("N"), "typed", Json($"{{\"Value\":{value}}}")))));
        }
    }
}
