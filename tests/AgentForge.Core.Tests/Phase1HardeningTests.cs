using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;
using Microsoft.Extensions.Logging;

namespace AgentForge.Core.Tests;

public sealed class Phase1HardeningTests
{
    [Fact]
    public void PublicContracts_CallerCollectionsMutate_SnapshotsRemainUnchanged()
    {
        var allowedTools = new HashSet<string>(StringComparer.Ordinal) { "echo" };
        var messages = new List<AgentMessage> { new UserMessage("original") };
        var calls = new[] { Call("call-1", "echo") };
        var agent = new AgentDefinition(
            new AgentId("agent"), "instructions", new ModelId("model"), allowedTools, new AgentExecutionLimits());
        var request = new AgentRunRequest(agent, messages);
        var assistant = AssistantMessage.FromToolCalls(calls);

        allowedTools.Clear();
        messages.Clear();
        calls[0] = Call("changed", "echo");

        Assert.Contains("echo", agent.AllowedTools);
        Assert.Single(request.Messages);
        Assert.Equal("call-1", assistant.ToolCalls.Single().Id);
        Assert.Throws<NotSupportedException>(() => ((ISet<string>)agent.AllowedTools).Add("changed"));
        Assert.Throws<NotSupportedException>(() => ((IList<AgentMessage>)request.Messages).Add(new UserMessage("changed")));
        Assert.Throws<NotSupportedException>(() => ((IList<ToolCall>)assistant.ToolCalls).Add(Call("changed", "echo")));
    }

    [Fact]
    public async Task RunAsync_ProviderAttemptsCollectionMutation_RuntimeStateIsReadOnly()
    {
        var tool = new DelegateTool("echo");
        var provider = new ScriptedAgentModelClient([
            (request, _) =>
            {
                Assert.Throws<NotSupportedException>(() =>
                    ((IList<AgentMessage>)request.Messages).Add(new UserMessage("injected")));
                Assert.Throws<NotSupportedException>(() =>
                    ((IList<ToolDescriptor>)request.AvailableTools).Clear());
                return Task.FromResult(ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("one", "echo"))));
            },
            (request, _) =>
            {
                var priorAssistant = Assert.IsType<AssistantMessage>(request.Messages[^2]);
                Assert.Throws<NotSupportedException>(() =>
                    ((IList<ToolCall>)priorAssistant.ToolCalls).Clear());
                return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("done")));
            }
        ]);

        var result = await Runtime(provider, tool).RunAsync(Request(["echo"]));

        Assert.Equal(5, result.Transcript.Count);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AgentMessage>)result.Transcript).Add(new UserMessage("changed")));
    }

    [Fact]
    public void JsonContracts_SourceDocumentDisposed_OwnTheirValues()
    {
        ToolDescriptor descriptor;
        ToolCall call;
        ToolResult result;
        using (var document = JsonDocument.Parse("{\"value\":42}"))
        {
            descriptor = new ToolDescriptor("tool", "description", document.RootElement);
            call = new ToolCall("id", "tool", document.RootElement);
            result = new ToolResult(document.RootElement);
        }

        Assert.Equal(42, descriptor.ParametersSchema.GetProperty("value").GetInt32());
        Assert.Equal(42, call.Arguments.GetProperty("value").GetInt32());
        Assert.Equal(42, result.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task RunAsync_DisposedProviderJson_IsInvalidModelResponse()
    {
        var document = JsonDocument.Parse("{}");
        var element = document.RootElement;
        document.Dispose();
        var provider = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(new ToolCall("id", "echo", element))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            Runtime(provider, new DelegateTool("echo")).RunAsync(Request(["echo"])));

        Assert.Equal(AgentExecutionErrorCode.InvalidModelResponse, exception.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_ProviderIgnoresCancellationAndReturns_StillCancels()
    {
        var entered = Signal();
        var release = Signal();
        var provider = new ScriptedAgentModelClient([async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return ModelResponse.Completed(AssistantMessage.FromText("must not complete"));
        }]);
        using var cancellation = new CancellationTokenSource();
        var run = Runtime(provider).RunAsync(Request(), cancellation.Token);
        await entered.Task;

        cancellation.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_CancelledByFirstTool_DoesNotInvokeSecondTool()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new DelegateTool("first", (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Json("null"));
        });
        var second = new DelegateTool("second");
        var provider = ScriptedAgentModelClient.FromResponses(ModelResponse.ToolCalls(
            AssistantMessage.FromToolCalls(Call("one", "first"), Call("two", "second"))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runtime(provider, first, second).RunAsync(Request(["first", "second"]), cancellation.Token));

        Assert.Equal(1, first.InvocationCount);
        Assert.Equal(0, second.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_NullToolResult_IsAgentToolException()
    {
        var tool = new DelegateTool("null-tool", (_, _) => Task.FromResult<JsonElement>(default), returnsNull: true);
        var provider = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("id", "null-tool"))));

        var exception = await Assert.ThrowsAsync<AgentToolException>(() =>
            Runtime(provider, tool).RunAsync(Request(["null-tool"])));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("null-tool", exception.ToolName);
        Assert.Equal("id", exception.ToolCallId);
    }

    [Fact]
    public async Task RunAsync_DisposedToolJson_IsAgentToolException()
    {
        var document = JsonDocument.Parse("{}");
        var element = document.RootElement;
        document.Dispose();
        var tool = new DelegateTool("disposed-tool", (_, _) => Task.FromResult(element));
        var provider = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("id", "disposed-tool"))));

        var exception = await Assert.ThrowsAsync<AgentToolException>(() =>
            Runtime(provider, tool).RunAsync(Request(["disposed-tool"])));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("disposed-tool", exception.ToolName);
        Assert.Equal("id", exception.ToolCallId);
    }

    [Fact]
    public async Task RunAsync_CumulativeToolBudgetExceeded_ExecutesOnlyEarlierBatch()
    {
        var tool = new DelegateTool("echo");
        var provider = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("one", "echo"))),
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("two", "echo"))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            Runtime(provider, tool).RunAsync(Request(["echo"], new AgentExecutionLimits(3, 1))));

        Assert.Equal(AgentExecutionErrorCode.ToolCallLimitExceeded, exception.ErrorCode);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_DuplicateCallIdAcrossResponses_IsInvalidModelResponse()
    {
        var tool = new DelegateTool("echo");
        var provider = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("same", "echo"))),
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("same", "echo"))));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            Runtime(provider, tool).RunAsync(Request(["echo"])));

        Assert.Equal(AgentExecutionErrorCode.InvalidModelResponse, exception.ErrorCode);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_UsageOverflow_IsInvalidModelResponseWithCause()
    {
        var tool = new DelegateTool("echo");
        var provider = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("one", "echo")), new ModelUsage(long.MaxValue, 0)),
            ModelResponse.Completed(AssistantMessage.FromText("done"), new ModelUsage(1, 0)));

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            Runtime(provider, tool).RunAsync(Request(["echo"])));

        Assert.Equal(AgentExecutionErrorCode.InvalidModelResponse, exception.ErrorCode);
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Fact]
    public async Task RunAsync_UnknownAllowedTool_RejectsBeforeProvider()
    {
        var providerCalled = false;
        var provider = new ScriptedAgentModelClient([(_, _) =>
        {
            providerCalled = true;
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("unused")));
        }]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Runtime(provider).RunAsync(Request(["missing"])));

        Assert.False(providerCalled);
    }

    [Fact]
    public async Task RunAsync_RegistryChangesAfterValidation_MissingToolHasExactErrorCode()
    {
        var provider = ScriptedAgentModelClient.FromResponses(
            ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("id", "echo"))));
        var registry = new VanishingToolRegistry(new DelegateTool("echo"));
        var runtime = new AgentRuntime(provider, registry);

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            runtime.RunAsync(Request(["echo"])));

        Assert.Equal(AgentExecutionErrorCode.ToolNotFound, exception.ErrorCode);
    }

    [Fact]
    public void AgentToolRegistry_InvalidRegistrations_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new AgentToolRegistry([new DelegateTool("")]));
        Assert.Throws<ArgumentException>(() => new AgentToolRegistry([
            new DelegateTool("duplicate"), new DelegateTool("duplicate")
        ]));
        Assert.Throws<ArgumentException>(() => new AgentToolRegistry([
            new DelegateTool("invalid-schema", schema: (JsonElement)default)
        ]));
    }

    [Fact]
    public void AgentToolRegistry_ToolDescriptorChanges_RegisteredSnapshotDoesNotChange()
    {
        var tool = new MutableDescriptorTool();
        var registry = new AgentToolRegistry([tool]);

        tool.ChangeDescriptor("changed");
        var descriptor = Assert.Single(registry.Describe(new HashSet<string>(["stable"], StringComparer.Ordinal)));

        Assert.Equal("stable", descriptor.Name);
        Assert.Equal("original", descriptor.Description);
    }

    [Fact]
    public async Task RunAsync_ProviderReceivesOnlyRegisteredAllowedDescriptors()
    {
        var provider = new ScriptedAgentModelClient([ (request, _) =>
        {
            var descriptor = Assert.Single(request.AvailableTools);
            Assert.Equal("allowed", descriptor.Name);
            return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("done")));
        }]);

        await Runtime(provider, new DelegateTool("allowed"), new DelegateTool("other"))
            .RunAsync(Request(["allowed"]));
    }

    [Fact]
    public async Task RunAsync_UntrustedValues_AreAbsentFromLogsAndExceptionMessage()
    {
        const string secretPrompt = "credential-prompt-9f4f";
        const string secretTool = "credential-tool-8d3e";
        const string secretCallId = "credential-call-7c2d";
        const string secretArgument = "credential-argument-6b1c";
        var logger = new RecordingLogger<AgentRuntime>();
        var tool = new DelegateTool(secretTool, (_, _) => Task.FromException<JsonElement>(new InvalidOperationException("internal")));
        var provider = ScriptedAgentModelClient.FromResponses(ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(
            new ToolCall(secretCallId, secretTool, Json($"{{\"value\":\"{secretArgument}\"}}")))));
        var request = Request([secretTool], message: secretPrompt);
        var runtime = new AgentRuntime(provider, new AgentToolRegistry([tool]), logger);

        var exception = await Assert.ThrowsAsync<AgentToolException>(() => runtime.RunAsync(request));
        var logged = string.Join(Environment.NewLine, logger.Messages);

        Assert.DoesNotContain(secretPrompt, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(secretTool, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(secretCallId, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(secretArgument, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(secretTool, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretCallId, exception.Message, StringComparison.Ordinal);
        Assert.Equal(secretTool, exception.ToolName);
        Assert.Equal(secretCallId, exception.ToolCallId);
    }

    [Fact]
    public async Task RunAsync_SecretToolResult_IsAbsentFromLogs()
    {
        const string secretResult = "credential-result-5a0b";
        var logger = new RecordingLogger<AgentRuntime>();
        var tool = new DelegateTool("echo", (_, _) =>
            Task.FromResult(Json($"{{\"value\":\"{secretResult}\"}}")));
        var provider = new ScriptedAgentModelClient([
            (_, _) => Task.FromResult(ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call("id", "echo")))),
            (_, _) => Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText("done")))
        ]);
        var runtime = new AgentRuntime(provider, new AgentToolRegistry([tool]), logger);

        await runtime.RunAsync(Request(["echo"]));

        Assert.DoesNotContain(secretResult, string.Join(Environment.NewLine, logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_LaterToolFails_EarlierSuccessfulSideEffectIsNotRolledBack()
    {
        var first = new DelegateTool("first");
        var second = new DelegateTool("second", (_, _) =>
            Task.FromException<JsonElement>(new InvalidOperationException("failure")));
        var provider = ScriptedAgentModelClient.FromResponses(ModelResponse.ToolCalls(
            AssistantMessage.FromToolCalls(Call("one", "first"), Call("two", "second"))));

        await Assert.ThrowsAsync<AgentToolException>(() =>
            Runtime(provider, first, second).RunAsync(Request(["first", "second"])));

        Assert.Equal(1, first.InvocationCount);
        Assert.Equal(1, second.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_ConcurrentToolExecutions_IsolateRunState()
    {
        var tool = new DelegateTool("echo");
        var provider = new PerRunToolModelClient();
        var runtime = Runtime(provider, tool);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(index => runtime.RunAsync(Request(["echo"], message: $"value-{index}"))));

        Assert.Equal(20, tool.InvocationCount);
        Assert.Equal(20, results.Select(result => result.ExecutionId).Distinct().Count());
        Assert.All(results, result =>
        {
            Assert.Equal(1, result.ToolCalls);
            Assert.Equal(5, result.Transcript.Count);
        });
    }

    private static AgentRuntime Runtime(IAgentModelClient provider, params IAgentTool[] tools) =>
        new(provider, new AgentToolRegistry(tools));

    private static AgentRunRequest Request(
        IEnumerable<string>? allowedTools = null,
        AgentExecutionLimits? limits = null,
        string message = "hello") => new(
            new AgentDefinition(
                new AgentId("agent"),
                "instructions",
                new ModelId("model"),
                allowedTools ?? [],
                limits ?? new AgentExecutionLimits()),
            [new UserMessage(message)]);

    private static ToolCall Call(string id, string name) => new(id, name, Json("{}"));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegateTool : IAgentTool
    {
        private readonly Func<ToolInvocation, CancellationToken, Task<JsonElement>> _invoke;
        private readonly bool _returnsNull;
        private int _invocationCount;

        public DelegateTool(
            string name,
            Func<ToolInvocation, CancellationToken, Task<JsonElement>>? invoke = null,
            JsonElement? schema = null,
            bool returnsNull = false)
        {
            Descriptor = new ToolDescriptor(name, "description", schema ?? Json("{\"type\":\"object\"}"));
            _invoke = invoke ?? ((_, _) => Task.FromResult(Json("null")));
            _returnsNull = returnsNull;
        }

        public ToolDescriptor Descriptor { get; }
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            var value = await _invoke(invocation, cancellationToken);
            return _returnsNull ? null! : new ToolResult(value);
        }
    }

    private sealed class MutableDescriptorTool : IAgentTool
    {
        private ToolDescriptor _descriptor = new("stable", "original", Json("{}"));
        public ToolDescriptor Descriptor => _descriptor;
        public void ChangeDescriptor(string name) => _descriptor = new ToolDescriptor(name, "changed", Json("{}"));
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult(Json("null")));
    }

    private sealed class VanishingToolRegistry(IAgentTool tool) : IAgentToolRegistry
    {
        private int _resolutions;

        public IReadOnlyList<ToolDescriptor> Describe(IReadOnlySet<string> allowedTools) =>
            Array.AsReadOnly(new[] { tool.Descriptor });

        public bool TryResolve(string name, out IAgentTool? resolved)
        {
            resolved = Interlocked.Increment(ref _resolutions) == 1 ? tool : null;
            return resolved is not null;
        }
    }

    private sealed class PerRunToolModelClient : IAgentModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toolResult = request.Messages.OfType<ToolResultMessage>().SingleOrDefault();
            return Task.FromResult(toolResult is null
                ? ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(
                    Call($"call-{request.ExecutionId}", "echo")))
                : ModelResponse.Completed(AssistantMessage.FromText("done")));
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
