#pragma warning disable OPENAI001 // Tests intentionally exercise the pinned experimental Responses SDK surface.
using System.Net;
using System.Text.Json;
using AgentForge.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentForge.Providers.OpenAI.Tests;

public sealed class OpenAIProviderTests
{
    [Fact]
    public async Task CompleteAsync_TextResponse_MapsResponseAndRequestPolicy()
    {
        var handler = new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, CompletedText("answer"))));
        var client = TestTransport.Create(handler);

        var response = await client.CompleteAsync(Request());

        Assert.Equal(ModelFinishReason.Completed, response.FinishReason);
        Assert.Equal("answer", response.Message.Text);
        Assert.Equal(new ModelUsage(2, 3), response.Usage);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.False(body.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("previous_response_id", out _));
        Assert.False(body.RootElement.TryGetProperty("conversation", out _));
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(5, false)]
    public async Task CompleteAsync_ProviderContentBoundary_IsInclusive(long maximum, bool accepted)
    {
        var handler = new TestHandler((_, _) => Task.FromResult(
            TestTransport.Json(HttpStatusCode.OK, CompletedText("secret"))));
        var task = TestTransport.Create(handler).CompleteAsync(
            Request(new(maxProviderContentCharacters: maximum)));

        if (accepted)
            Assert.Equal("secret", (await task).Message.Text);
        else
        {
            var exception = await Assert.ThrowsAsync<ModelProviderException>(() => task);
            Assert.Equal(ModelProviderErrorKind.ProviderProcessingLimitExceeded, exception.Kind);
            Assert.False(exception.IsTransient);
            Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        }
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_TranscriptAndTools_PreserveOrderAndCorrelation()
    {
        var handler = new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, CompletedText("done"))));
        var client = TestTransport.Create(handler);
        using var args = JsonDocument.Parse("{\"value\":7}");
        using var result = JsonDocument.Parse("{\"ok\":true}");
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var request = new ModelRequest(ExecutionId.New(), new ModelId("gpt-test"),
        [
            new SystemMessage("system-sentinel"), new UserMessage("user-sentinel"),
            AssistantMessage.FromToolCalls(new ToolCall("call-1", "demo", args.RootElement)),
            new ToolResultMessage("call-1", "demo", result.RootElement)
        ], [new ToolDescriptor("demo", "description", schema.RootElement)]);

        await client.CompleteAsync(request);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var input = body.RootElement.GetProperty("input");
        Assert.Equal(new[] { "message", "message", "function_call", "function_call_output" },
            input.EnumerateArray().Select(x => x.GetProperty("type").GetString()));
        Assert.Equal("call-1", input[2].GetProperty("call_id").GetString());
        Assert.Equal("call-1", input[3].GetProperty("call_id").GetString());
        var tool = Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("demo", tool.GetProperty("name").GetString());
        Assert.False(tool.GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task CompleteAsync_DottedFunctionName_RejectsBeforeTransport()
    {
        var handler = new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, CompletedText("unused"))));
        var client = TestTransport.Create(handler);
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var request = new ModelRequest(
            ExecutionId.New(),
            new ModelId("gpt-test"),
            [new UserMessage("content-sentinel")],
            [new ToolDescriptor("calculator.add", "description", schema.RootElement)]);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => client.CompleteAsync(request));

        Assert.Equal(ModelProviderErrorKind.InvalidRequest, exception.Kind);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain("calculator.add", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("content-sentinel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_FunctionCall_UsesCallIdAndOwnedArguments()
    {
        const string json = """
        {"id":"r","object":"response","created_at":1,"status":"completed","model":"gpt-test","output":[{"type":"function_call","id":"item-9","call_id":"call-7","name":"demo","arguments":"{\"value\":7}","status":"completed"}],"parallel_tool_calls":false,"tools":[],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2,"input_tokens_details":{"cached_tokens":0},"output_tokens_details":{"reasoning_tokens":0}}}
        """;
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, json))));

        var response = await client.CompleteAsync(Request());

        var call = Assert.Single(response.Message.ToolCalls);
        Assert.Equal("call-7", call.Id);
        Assert.Equal(7, call.Arguments.GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData("max_output_tokens", ModelFinishReason.LengthLimit)]
    [InlineData("content_filter", ModelFinishReason.ContentFiltered)]
    [InlineData("future_reason", ModelFinishReason.Unknown)]
    public async Task CompleteAsync_Incomplete_MapsReason(string reason, ModelFinishReason expected)
    {
        var json = """{"id":"r","object":"response","created_at":1,"status":"incomplete","model":"gpt-test","incomplete_details":{"reason":"REASON"},"output":[],"parallel_tool_calls":false,"tools":[]}"""
            .Replace("REASON", reason, StringComparison.Ordinal);
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, json))));
        Assert.Equal(expected, (await client.CompleteAsync(Request())).FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_Refusal_MapsWithoutExposingText()
    {
        const string json = """{"id":"r","object":"response","created_at":1,"status":"completed","model":"gpt-test","output":[{"type":"message","id":"m","status":"completed","role":"assistant","content":[{"type":"refusal","refusal":"sensitive-refusal"}]}],"parallel_tool_calls":false,"tools":[]}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, json))));
        var response = await client.CompleteAsync(Request());
        Assert.Equal(ModelFinishReason.Refused, response.FinishReason);
        Assert.Null(response.Message.Text);
    }

    [Fact]
    public async Task CompleteAsync_RetryableHttpError_AttemptsOnceAndSanitizes()
    {
        const string secret = "provider-secret-body";
        var handler = new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.ServiceUnavailable, $"{{\"error\":{{\"message\":\"{secret}\"}}}}")));
        var client = TestTransport.Create(handler);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => client.CompleteAsync(Request()));

        Assert.Equal(ModelProviderErrorKind.ServiceUnavailable, exception.Kind);
        Assert.True(exception.IsTransient);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ModelProviderErrorKind.Authentication, false)]
    [InlineData(HttpStatusCode.Forbidden, ModelProviderErrorKind.Authorization, false)]
    [InlineData(HttpStatusCode.RequestTimeout, ModelProviderErrorKind.Timeout, true)]
    [InlineData((HttpStatusCode)429, ModelProviderErrorKind.RateLimited, true)]
    [InlineData(HttpStatusCode.BadRequest, ModelProviderErrorKind.InvalidRequest, false)]
    [InlineData(HttpStatusCode.NotFound, ModelProviderErrorKind.InvalidRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, ModelProviderErrorKind.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadGateway, ModelProviderErrorKind.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ModelProviderErrorKind.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, ModelProviderErrorKind.ServiceUnavailable, true)]
    [InlineData((HttpStatusCode)418, ModelProviderErrorKind.Unknown, false)]
    public async Task CompleteAsync_HttpError_Classifies(HttpStatusCode status, ModelProviderErrorKind kind, bool transient)
    {
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Json(status, "{\"error\":{\"message\":\"hidden\"}}"))));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => client.CompleteAsync(Request()));
        Assert.Equal(kind, exception.Kind);
        Assert.Equal(transient, exception.IsTransient);
    }

    [Fact]
    public async Task StreamAsync_TextAndTerminal_EmitsCompletionAfterDeltas()
    {
        var delta = """{"type":"response.output_text.delta","sequence_number":1,"item_id":"m","output_index":0,"content_index":0,"delta":"hel","logprobs":[]}""";
        var delta2 = """{"type":"response.output_text.delta","sequence_number":2,"item_id":"m","output_index":0,"content_index":0,"delta":"lo","logprobs":[]}""";
        var terminal = $$"""{"type":"response.completed","sequence_number":3,"response":{{CompletedText("hello")}}}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Sse(delta, delta2, terminal))));

        var events = await CollectAsync(client.StreamAsync(Request()));

        Assert.Equal("hel", Assert.IsType<ModelTextDelta>(events[0]).Text);
        Assert.Equal("lo", Assert.IsType<ModelTextDelta>(events[1]).Text);
        var completed = Assert.IsType<ModelResponseCompleted>(events[2]).Response;
        Assert.Equal("hello", completed.Message.Text);
        Assert.Equal(new ModelUsage(2, 3), completed.Usage);
    }

    [Fact]
    public async Task StreamAsync_EventOverflow_StopsBeforeExcessEventAndCompletion()
    {
        var delta = """{"type":"response.output_text.delta","sequence_number":1,"item_id":"m","output_index":0,"content_index":0,"delta":"ok","logprobs":[]}""";
        var terminal = $$"""{"type":"response.completed","sequence_number":2,"response":{{CompletedText("ok")}}}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Sse(delta, terminal))));
        var observed = new List<ModelStreamEvent>();

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var item in client.StreamAsync(Request(new(maxProviderStreamEvents: 1))))
                observed.Add(item);
        });

        Assert.Equal(ModelProviderErrorKind.ProviderProcessingLimitExceeded, exception.Kind);
        Assert.Equal("ok", Assert.IsType<ModelTextDelta>(Assert.Single(observed)).Text);
    }

    [Fact]
    public async Task StreamAsync_EofWithoutTerminal_ThrowsProtocol()
    {
        var delta = """{"type":"response.output_text.delta","sequence_number":1,"item_id":"m","output_index":0,"content_index":0,"delta":"partial","logprobs":[]}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Sse(delta))));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(client.StreamAsync(Request())));
        Assert.Equal(ModelProviderErrorKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task StreamAsync_DuplicateTerminal_ThrowsProtocolWithoutCompletion()
    {
        var terminal = $$"""{"type":"response.completed","sequence_number":1,"response":{{CompletedText("ok")}}}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Sse(terminal, terminal))));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(client.StreamAsync(Request())));
        Assert.Equal(ModelProviderErrorKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task CompleteAsync_PreCancelled_DoesNotInvokeTransport()
    {
        var handler = new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, CompletedText("no"))));
        var client = TestTransport.Create(handler);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync(Request(), source.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_CancelledResponse_MapsFinishReason()
    {
        const string json = """{"id":"r","object":"response","created_at":1,"status":"cancelled","model":"gpt-test","output":[],"parallel_tool_calls":false,"tools":[]}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, json))));
        Assert.Equal(ModelFinishReason.Cancelled, (await client.CompleteAsync(Request())).FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_FailedResponse_ClassifiesSafeProviderCode()
    {
        const string json = """{"id":"r","object":"response","created_at":1,"status":"failed","model":"gpt-test","error":{"code":"rate_limit_exceeded","message":"secret-provider-message"},"output":[],"parallel_tool_calls":false,"tools":[]}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, json))));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => client.CompleteAsync(Request()));
        Assert.Equal(ModelProviderErrorKind.RateLimited, exception.Kind);
        Assert.DoesNotContain("secret-provider-message", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_InconsistentUsage_ThrowsInvalidUsage()
    {
        var json = CompletedText("ok").Replace("\"total_tokens\":5", "\"total_tokens\":99", StringComparison.Ordinal);
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, json))));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => client.CompleteAsync(Request()));
        Assert.Equal(ModelProviderErrorKind.TokenUsageUnavailableOrInvalid, exception.Kind);
    }

    [Fact]
    public async Task CompleteAsync_UnsupportedOutput_ThrowsProtocol()
    {
        const string json = """{"id":"r","object":"response","created_at":1,"status":"completed","model":"gpt-test","output":[{"type":"reasoning","id":"reason","summary":[]}],"parallel_tool_calls":false,"tools":[]}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, json))));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => client.CompleteAsync(Request()));
        Assert.Equal(ModelProviderErrorKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task CompleteAsync_CancellationDuringRequest_Propagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestTransport.Json(HttpStatusCode.OK, CompletedText("never"));
        });
        var client = TestTransport.Create(handler);
        using var source = new CancellationTokenSource();
        var task = client.CompleteAsync(Request(), source.Token);
        await entered.Task;
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task StreamAsync_FunctionFragmentsRemainPrivateAndMatchTerminal()
    {
        var delta = """{"type":"response.function_call_arguments.delta","sequence_number":1,"item_id":"item","output_index":0,"delta":"{\"value\":"}""";
        var delta2 = """{"type":"response.function_call_arguments.delta","sequence_number":2,"item_id":"item","output_index":0,"delta":"7}"}""";
        var done = """{"type":"response.function_call_arguments.done","sequence_number":3,"item_id":"item","output_index":0,"name":"demo","arguments":"{\"value\":7}"}""";
        const string result = """{"id":"r","object":"response","created_at":1,"status":"completed","model":"gpt-test","output":[{"type":"function_call","id":"item","call_id":"call","name":"demo","arguments":"{\"value\":7}","status":"completed"}],"parallel_tool_calls":false,"tools":[]}""";
        var terminal = $$"""{"type":"response.completed","sequence_number":4,"response":{{result}}}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Sse(delta, delta2, done, terminal))));

        var events = await CollectAsync(client.StreamAsync(Request()));

        var completion = Assert.IsType<ModelResponseCompleted>(Assert.Single(events));
        Assert.Equal(7, Assert.Single(completion.Response.Message.ToolCalls).Arguments.GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData(11, true)]
    [InlineData(10, false)]
    public async Task StreamAsync_FragmentedArgumentsUseCumulativeUtf8Budget(long maximum, bool accepted)
    {
        var delta = """{"type":"response.function_call_arguments.delta","sequence_number":1,"item_id":"item","output_index":0,"delta":"{\"value\":"}""";
        var delta2 = """{"type":"response.function_call_arguments.delta","sequence_number":2,"item_id":"item","output_index":0,"delta":"7}"}""";
        var done = """{"type":"response.function_call_arguments.done","sequence_number":3,"item_id":"item","output_index":0,"name":"demo","arguments":"{\"value\":7}"}""";
        const string result = """{"id":"r","object":"response","created_at":1,"status":"completed","model":"gpt-test","output":[{"type":"function_call","id":"item","call_id":"call","name":"demo","arguments":"{\"value\":7}","status":"completed"}],"parallel_tool_calls":false,"tools":[]}""";
        var terminal = $$"""{"type":"response.completed","sequence_number":4,"response":{{result}}}""";
        var task = CollectAsync(TestTransport.Create(new TestHandler((_, _) => Task.FromResult(
            TestTransport.Sse(delta, delta2, done, terminal)))).StreamAsync(
                Request(new(maxProviderToolArgumentBytes: maximum))));

        if (accepted)
            Assert.IsType<ModelResponseCompleted>(Assert.Single(await task));
        else
            Assert.Equal(ModelProviderErrorKind.ProviderProcessingLimitExceeded,
                (await Assert.ThrowsAsync<ModelProviderException>(() => task)).Kind);
    }

    [Fact]
    public async Task StreamAsync_ConflictingFunctionFragments_ThrowsProtocol()
    {
        var delta = """{"type":"response.function_call_arguments.delta","sequence_number":1,"item_id":"item","output_index":0,"delta":"{}"}""";
        var done = """{"type":"response.function_call_arguments.done","sequence_number":2,"item_id":"item","output_index":0,"name":"demo","arguments":"{\"value\":7}"}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Sse(delta, done))));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(client.StreamAsync(Request())));
        Assert.Equal(ModelProviderErrorKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task StreamAsync_MalformedEvent_IsSanitized()
    {
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.Sse("{malformed-sensitive-json"))));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(client.StreamAsync(Request())));
        Assert.DoesNotContain("malformed-sensitive-json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsync_CancellationDuringTransport_Propagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestTransport.Sse();
        });
        using var source = new CancellationTokenSource();
        var collection = CollectAsync(TestTransport.Create(handler).StreamAsync(Request(), source.Token));
        await entered.Task;
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection);
    }

    [Fact]
    public async Task StreamAsync_FragmentedSseBytes_ParsesWireStream()
    {
        var delta = """{"type":"response.output_text.delta","sequence_number":1,"item_id":"m","output_index":0,"content_index":0,"delta":"ok","logprobs":[]}""";
        var terminal = $$"""{"type":"response.completed","sequence_number":2,"response":{{CompletedText("ok")}}}""";
        var client = TestTransport.Create(new TestHandler((_, _) => Task.FromResult(TestTransport.FragmentedSse(delta, terminal))));
        var events = await CollectAsync(client.StreamAsync(Request()));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task CompleteAsync_ModelNotFoundAndSafeHeaders_MapMetadata()
    {
        var handler = new TestHandler((_, _) =>
        {
            var response = TestTransport.Json(HttpStatusCode.NotFound,
                "{\"error\":{\"code\":\"model_not_found\",\"param\":\"model\",\"message\":\"hidden\"}}",
                "req_safe-123");
            response.Headers.TryAddWithoutValidation("Retry-After", "2");
            return Task.FromResult(response);
        });
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => TestTransport.Create(handler).CompleteAsync(Request()));
        Assert.Equal(ModelProviderErrorKind.ModelNotFound, exception.Kind);
        Assert.Equal("req_safe-123", exception.ProviderRequestId);
        Assert.Equal(TimeSpan.FromSeconds(2), exception.RetryAfter);
    }

    [Fact]
    public async Task CompleteAsync_UnsafeRequestId_IsDiscarded()
    {
        var handler = new TestHandler((_, _) => Task.FromResult(TestTransport.Json(
            HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"hidden\"}}", "unsafe id")));
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => TestTransport.Create(handler).CompleteAsync(Request()));
        Assert.Null(exception.ProviderRequestId);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("not-a-delay")]
    public async Task CompleteAsync_InvalidRetryAfter_IsDiscarded(string retryAfter)
    {
        var handler = new TestHandler((_, _) =>
        {
            var response = TestTransport.Json(
                HttpStatusCode.TooManyRequests,
                "{\"error\":{\"message\":\"hidden\"}}");
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        });

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            TestTransport.Create(handler).CompleteAsync(Request()));

        Assert.Null(exception.RetryAfter);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CompleteAsync_ConcurrentCalls_DoNotShareState()
    {
        var handler = new TestHandler((_, _) => Task.FromResult(TestTransport.Json(HttpStatusCode.OK, CompletedText("ok"))));
        var client = TestTransport.Create(handler);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.CompleteAsync(Request())));
        Assert.All(responses, response => Assert.Equal("ok", response.Message.Text));
        Assert.Equal(8, handler.Calls);
    }

    [Fact]
    public void Options_InvalidEndpointsAndCredentials_FailSafely()
    {
        Assert.Throws<ArgumentException>(() => new OpenAIAgentModelClient(new OpenAIProviderOptions()));
        Assert.Throws<ArgumentException>(() => new OpenAIAgentModelClient(new OpenAIProviderOptions { ApiKey = "secret", Endpoint = new Uri("http://example.com") }));
        _ = new OpenAIAgentModelClient(new OpenAIProviderOptions { ApiKey = "secret", Endpoint = new Uri("http://localhost:1234"), AllowInsecureLoopbackEndpoint = true });
    }

    [Fact]
    public void AddOpenAIModelClient_RegistersBothContracts()
    {
        var services = new ServiceCollection();
        services.AddOpenAIModelClient(options => options.ApiKey = "synthetic-key");
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<IAgentModelClient>(), provider.GetRequiredService<IStreamingAgentModelClient>());
    }

    private static ModelRequest Request(AgentResourceLimits? resources = null) => new(
        ExecutionId.New(), new ModelId("gpt-test"), [new UserMessage("hello")], [], resources);

    private static async Task<List<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private static string CompletedText(string text) => """
    {"id":"r","object":"response","created_at":1,"status":"completed","model":"gpt-test","output":[{"type":"message","id":"m","status":"completed","role":"assistant","content":[{"type":"output_text","text":TEXT,"annotations":[],"logprobs":[]}]}],"parallel_tool_calls":false,"tools":[],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5,"input_tokens_details":{"cached_tokens":0},"output_tokens_details":{"reasoning_tokens":0}}}
    """.Replace("TEXT", JsonSerializer.Serialize(text), StringComparison.Ordinal);
}
