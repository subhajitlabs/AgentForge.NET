using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentForge.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentForge.Adapters.MicrosoftExtensionsAI.Tests;

public sealed class MicrosoftExtensionsAIModelClientTests
{
    [Fact]
    public async Task CompleteAsync_TextCompletion_MapsFinishAndUsage()
    {
        var chat = new ScriptedChatClient(complete: (_, _, _) => Task.FromResult(
            Response(new ChatMessage(ChatRole.Assistant, "answer"), ChatFinishReason.Stop,
                new UsageDetails { InputTokenCount = 2, OutputTokenCount = 3, TotalTokenCount = 5 })));

        var response = await new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(Request());

        Assert.Equal(ModelFinishReason.Completed, response.FinishReason);
        Assert.Equal("answer", response.Message.Text);
        Assert.Equal(new ModelUsage(2, 3), response.Usage);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(5, false)]
    public async Task CompleteAsync_ProviderContentBoundary_IsInclusive(long maximum, bool accepted)
    {
        var chat = new ScriptedChatClient(complete: (_, _, _) => Task.FromResult(
            Response(new ChatMessage(ChatRole.Assistant, "secret"), ChatFinishReason.Stop)));
        var task = new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(
            Request(resources: new(maxProviderContentCharacters: maximum)));

        if (accepted)
            Assert.Equal("secret", (await task).Message.Text);
        else
        {
            var exception = await Assert.ThrowsAsync<ModelProviderException>(() => task);
            Assert.Equal(ModelProviderErrorKind.ProviderProcessingLimitExceeded, exception.Kind);
            Assert.False(exception.IsTransient);
            Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        }
        Assert.Equal(1, chat.CompleteCalls);
    }

    [Fact]
    public async Task CompleteAsync_MultipleToolCalls_PreservesIdsNamesAndArguments()
    {
        var message = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-1", "first", new Dictionary<string, object?> { ["value"] = 1 }),
            new FunctionCallContent("call-2", "second", new Dictionary<string, object?> { ["flag"] = true })
        ]);
        var chat = new ScriptedChatClient(complete: (_, _, _) =>
            Task.FromResult(Response(message, ChatFinishReason.ToolCalls)));

        var response = await new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(Request());

        Assert.Equal(ModelFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal(new[] { "call-1", "call-2" }, response.Message.ToolCalls.Select(call => call.Id));
        Assert.Equal(1, response.Message.ToolCalls[0].Arguments.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_TranscriptAndTools_MapFaithfullyWithoutExecutableDelegates()
    {
        var chat = new ScriptedChatClient(complete: (_, _, _) =>
            Task.FromResult(Response(new ChatMessage(ChatRole.Assistant, "done"), ChatFinishReason.Stop)));
        using var arguments = JsonDocument.Parse("{\"number\":7}");
        using var result = JsonDocument.Parse("{\"ok\":true}");
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"number\":{\"type\":\"integer\"}}}");
        var request = new ModelRequest(ExecutionId.New(), new ModelId("model"),
        [
            new SystemMessage("system"), new UserMessage("user"),
            AssistantMessage.FromToolCalls(new ToolCall("call", "demo", arguments.RootElement)),
            new ToolResultMessage("call", "demo", result.RootElement)
        ], [new ToolDescriptor("demo", "description", schema.RootElement)]);

        await new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(request);

        var mappedMessages = Assert.IsAssignableFrom<IReadOnlyList<ChatMessage>>(chat.LastMessages);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool },
            mappedMessages.Select(message => message.Role));
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(mappedMessages[2].Contents));
        Assert.Equal("call", call.CallId);
        var functionResult = Assert.IsType<FunctionResultContent>(Assert.Single(mappedMessages[3].Contents));
        Assert.Equal("call", functionResult.CallId);
        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(chat.LastOptions!.Tools!));
        Assert.False(declaration is AIFunction);
        Assert.Equal("demo", declaration.Name);
        Assert.Equal("description", declaration.Description);
        Assert.Equal(JsonValueKind.Object, declaration.JsonSchema.ValueKind);
    }

    [Theory]
    [InlineData("length", ModelFinishReason.LengthLimit)]
    [InlineData("content_filter", ModelFinishReason.ContentFiltered)]
    [InlineData("future_reason", ModelFinishReason.Unknown)]
    public async Task CompleteAsync_NonSuccessFinishReason_MapsExplicitly(
        string reason, ModelFinishReason expected)
    {
        var chat = new ScriptedChatClient(complete: (_, _, _) => Task.FromResult(
            Response(new ChatMessage(ChatRole.Assistant, "partial"), new ChatFinishReason(reason))));
        Assert.Equal(expected,
            (await new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(Request())).FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_MissingFinishReason_DoesNotInferSuccess()
    {
        var chat = new ScriptedChatClient(complete: (_, _, _) => Task.FromResult(
            Response(new ChatMessage(ChatRole.Assistant, "text"), null)));
        var response = await new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(Request());
        Assert.Equal(ModelFinishReason.Unknown, response.FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_MixedTextAndToolCall_ThrowsProtocolWithoutPayload()
    {
        const string secret = "sensitive-argument";
        var message = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("text"),
            new FunctionCallContent("call", "demo", new Dictionary<string, object?> { ["value"] = secret })
        ]);
        var chat = new ScriptedChatClient(complete: (_, _, _) =>
            Task.FromResult(Response(message, ChatFinishReason.ToolCalls)));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(Request()));

        Assert.Equal(ModelProviderErrorKind.Protocol, exception.Kind);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_InvalidUsage_ThrowsInvalidUsage()
    {
        var chat = new ScriptedChatClient(complete: (_, _, _) => Task.FromResult(
            Response(new ChatMessage(ChatRole.Assistant, "answer"), ChatFinishReason.Stop,
                new UsageDetails { InputTokenCount = 2, OutputTokenCount = 3, TotalTokenCount = 99 })));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(Request()));
        Assert.Equal(ModelProviderErrorKind.TokenUsageUnavailableOrInvalid, exception.Kind);
    }

    [Fact]
    public async Task CompleteAsync_ProviderFailure_IsSanitizedAndNotRetried()
    {
        const string secret = "provider-secret";
        var chat = new ScriptedChatClient(complete: (_, _, _) =>
            throw new HttpRequestException(secret, null, System.Net.HttpStatusCode.TooManyRequests));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(Request()));
        Assert.Equal(ModelProviderErrorKind.RateLimited, exception.Kind);
        Assert.True(exception.IsTransient);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, chat.CompleteCalls);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, ModelProviderErrorKind.Authentication, false)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, ModelProviderErrorKind.Authorization, false)]
    [InlineData(System.Net.HttpStatusCode.BadRequest, ModelProviderErrorKind.InvalidRequest, false)]
    [InlineData(System.Net.HttpStatusCode.NotFound, ModelProviderErrorKind.InvalidRequest, false)]
    [InlineData(System.Net.HttpStatusCode.RequestTimeout, ModelProviderErrorKind.Timeout, true)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, ModelProviderErrorKind.RateLimited, true)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, ModelProviderErrorKind.ServiceUnavailable, true)]
    [InlineData((System.Net.HttpStatusCode)418, ModelProviderErrorKind.Unknown, false)]
    public async Task CompleteAsync_HttpFailure_ClassifiesConservatively(
        System.Net.HttpStatusCode status,
        ModelProviderErrorKind expectedKind,
        bool expectedTransient)
    {
        var chat = new ScriptedChatClient(complete: (_, _, _) =>
            throw new HttpRequestException("sensitive-provider-message", null, status));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(Request()));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(expectedTransient, exception.IsTransient);
        Assert.DoesNotContain("sensitive-provider-message", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, chat.CompleteCalls);
    }

    [Fact]
    public async Task CompleteAsync_UnsupportedClient_ReportsCapabilityFailure()
    {
        var request = Request();
        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            new MicrosoftExtensionsAIModelClient(new ScriptedChatClient()).CompleteAsync(request));
        Assert.Equal(AgentExecutionErrorCode.CapabilityNotSupported, exception.ErrorCode);
        Assert.Equal(request.ExecutionId, exception.ExecutionId);
    }

    [Fact]
    public async Task CompleteAsync_ResultArguments_AreOwned()
    {
        var arguments = new Dictionary<string, object?> { ["value"] = 1 };
        var message = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call", "demo", arguments)]);
        var chat = new ScriptedChatClient(complete: (_, _, _) =>
            Task.FromResult(Response(message, ChatFinishReason.ToolCalls)));
        var response = await new MicrosoftExtensionsAIModelClient(chat).CompleteAsync(Request());
        arguments["value"] = 99;
        Assert.Equal(1, response.Message.ToolCalls[0].Arguments.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task StreamAsync_TextUpdatesAndUsage_CompleteAfterCleanEnd()
    {
        var usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 2, TotalTokenCount = 3 };
        var chat = new ScriptedChatClient(stream: (_, _, token) => Stream(token,
            Update("hel", "m"), Update("lo", "m", ChatFinishReason.Stop),
            new ChatResponseUpdate(null, [new UsageContent(usage)]) { MessageId = "m" }));

        var events = await CollectAsync(new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request()));

        Assert.Equal("hel", Assert.IsType<ModelTextDelta>(events[0]).Text);
        Assert.Equal("lo", Assert.IsType<ModelTextDelta>(events[1]).Text);
        var completed = Assert.IsType<ModelResponseCompleted>(events[2]);
        Assert.Equal("hello", completed.Response.Message.Text);
        Assert.Equal(new ModelUsage(1, 2), completed.Response.Usage);
    }

    [Fact]
    public async Task StreamAsync_DuplicateUsageSnapshots_AreNotDoubleCounted()
    {
        var usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 2, TotalTokenCount = 3 };
        var chat = new ScriptedChatClient(stream: (_, _, token) => Stream(token,
            Update("done", "m", ChatFinishReason.Stop),
            new ChatResponseUpdate(null, [new UsageContent(usage)]) { MessageId = "m" },
            new ChatResponseUpdate(null, [new UsageContent(usage)]) { MessageId = "m" }));

        var events = await CollectAsync(new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request()));

        Assert.Equal(new ModelUsage(1, 2), Assert.IsType<ModelResponseCompleted>(events[^1]).Response.Usage);
    }

    [Fact]
    public async Task StreamAsync_EventOverflow_StopsBeforeExcessEventAndCompletion()
    {
        var chat = new ScriptedChatClient(stream: (_, _, token) => Stream(token,
            Update("a", "m"), Update("b", "m", ChatFinishReason.Stop)));
        var observed = new List<ModelStreamEvent>();

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var item in new MicrosoftExtensionsAIModelClient(chat).StreamAsync(
                Request(resources: new(maxProviderStreamEvents: 1)))) observed.Add(item);
        });

        Assert.Equal(ModelProviderErrorKind.ProviderProcessingLimitExceeded, exception.Kind);
        Assert.Equal("a", Assert.IsType<ModelTextDelta>(Assert.Single(observed)).Text);
    }

    [Fact]
    public async Task StreamAsync_MultipleFunctionCallsUseCumulativeUtf8ArgumentBudget()
    {
        var update = new ChatResponseUpdate(ChatRole.Assistant,
        [
            new FunctionCallContent("one", "first", new Dictionary<string, object?> { ["x"] = "é" }),
            new FunctionCallContent("two", "second", new Dictionary<string, object?> { ["x"] = "é" })
        ])
        { MessageId = "m", FinishReason = ChatFinishReason.ToolCalls };
        var chat = new ScriptedChatClient(stream: (_, _, token) => Stream(token, update));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(
            new MicrosoftExtensionsAIModelClient(chat).StreamAsync(
                Request(resources: new(maxProviderToolArgumentBytes: 10)))));

        Assert.Equal(ModelProviderErrorKind.ProviderProcessingLimitExceeded, exception.Kind);
    }

    [Fact]
    public async Task StreamAsync_CompleteToolCalls_RemainPrivateUntilCompletion()
    {
        var update = new ChatResponseUpdate(ChatRole.Assistant,
        [
            new FunctionCallContent("one", "first", new Dictionary<string, object?>()),
            new FunctionCallContent("two", "second", new Dictionary<string, object?>())
        ])
        { MessageId = "m", FinishReason = ChatFinishReason.ToolCalls };
        var chat = new ScriptedChatClient(stream: (_, _, token) => Stream(token, update));

        var events = await CollectAsync(new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request()));

        var completion = Assert.IsType<ModelResponseCompleted>(Assert.Single(events));
        Assert.Equal(2, completion.Response.Message.ToolCalls.Count);
    }

    [Fact]
    public async Task StreamAsync_UnrepresentableFunctionArguments_ThrowProtocol()
    {
        var arguments = new Dictionary<string, object?>();
        arguments["self"] = arguments;
        var update = new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("call", "demo", arguments)])
        { MessageId = "m", FinishReason = ChatFinishReason.ToolCalls };
        var chat = new ScriptedChatClient(stream: (_, _, token) => Stream(token, update));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            CollectAsync(new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request())));

        Assert.Equal(ModelProviderErrorKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task StreamAsync_MissingFinishReason_ThrowsProtocol()
    {
        var chat = new ScriptedChatClient(stream: (_, _, token) => Stream(token, Update("partial", "m")));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            CollectAsync(new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request())));
        Assert.Equal(ModelProviderErrorKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task StreamAsync_ConflictingFinishReasons_ThrowsProtocol()
    {
        var chat = new ScriptedChatClient(stream: (_, _, token) => Stream(token,
            Update("done", "m", ChatFinishReason.Stop),
            new ChatResponseUpdate { FinishReason = ChatFinishReason.Length }));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            CollectAsync(new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request())));
        Assert.Equal(ModelProviderErrorKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task StreamAsync_ExceptionAfterPartialOutput_IsSanitizedAndHasNoCompletion()
    {
        const string secret = "provider-stream-secret";
        var chat = new ScriptedChatClient(stream: (_, _, token) => FailingStream(token, secret));
        var observed = new List<ModelStreamEvent>();
        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var item in new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request()))
                observed.Add(item);
        });
        Assert.Single(observed);
        Assert.IsType<ModelTextDelta>(observed[0]);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsync_PreCancellation_DoesNotInvokeClient()
    {
        var chat = new ScriptedChatClient();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CollectAsync(new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request(), source.Token)));
        Assert.Equal(0, chat.StreamCalls);
    }

    [Fact]
    public async Task StreamAsync_CancellationDuringEnumeration_Propagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new ScriptedChatClient(stream: (_, _, token) => WaitingStream(entered, token));
        using var source = new CancellationTokenSource();
        var task = CollectAsync(new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request(), source.Token));
        await entered.Task;
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task StreamAsync_EarlyDisposal_DisposesProviderEnumerator()
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new ScriptedChatClient(stream: (_, _, token) => DisposalStream(disposed, token));
        await foreach (var item in new MicrosoftExtensionsAIModelClient(chat).StreamAsync(Request()))
        {
            Assert.IsType<ModelTextDelta>(item);
            break;
        }
        await disposed.Task;
    }

    [Fact]
    public async Task CompleteAsync_ConcurrentCalls_RemainIsolated()
    {
        var chat = new ScriptedChatClient(complete: (messages, _, _) =>
            Task.FromResult(Response(new ChatMessage(ChatRole.Assistant,
                messages.Single().Text!), ChatFinishReason.Stop)));
        var client = new MicrosoftExtensionsAIModelClient(chat);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            client.CompleteAsync(Request(index.ToString()))));
        Assert.Equal(Enumerable.Range(0, 8).Select(index => index.ToString()).Order(),
            results.Select(result => result.Message.Text!).Order());
    }

    [Fact]
    public void AddMicrosoftExtensionsAIModelClient_RegistersSingletonAndRejectsConflict()
    {
        var services = new ServiceCollection();
        services.AddMicrosoftExtensionsAIModelClient(new ScriptedChatClient());
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<IAgentModelClient>(),
            provider.GetRequiredService<IStreamingAgentModelClient>());
        Assert.Throws<InvalidOperationException>(() =>
            services.AddMicrosoftExtensionsAIModelClient(new ScriptedChatClient()));
    }

    private static ChatResponse Response(
        ChatMessage message, ChatFinishReason? finishReason, UsageDetails? usage = null) =>
        new(message) { FinishReason = finishReason, Usage = usage };

    private static ChatResponseUpdate Update(
        string text, string messageId, ChatFinishReason? finishReason = null) =>
        new(ChatRole.Assistant, text) { MessageId = messageId, FinishReason = finishReason };

    private static ModelRequest Request(string text = "hello", AgentResourceLimits? resources = null) =>
        new(ExecutionId.New(), new ModelId("model"), [new UserMessage(text)], [], resources);

    private static async Task<List<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(
        [EnumeratorCancellation] CancellationToken cancellationToken, params ChatResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailingStream(
        [EnumeratorCancellation] CancellationToken cancellationToken, string message)
    {
        yield return Update("partial", "m");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(message);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitingStream(
        TaskCompletionSource entered,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        entered.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> DisposalStream(
        TaskCompletionSource disposed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            yield return Update("partial", "m");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally { disposed.SetResult(); }
    }
}
