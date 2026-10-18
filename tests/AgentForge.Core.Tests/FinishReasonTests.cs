using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;

namespace AgentForge.Core.Tests;

public sealed class FinishReasonTests
{
    [Fact]
    public void ModelResponse_CompletedFactory_SetsExplicitReason()
    {
        var response = ModelResponse.Completed(AssistantMessage.FromText("done"), new ModelUsage(1, 2));

        Assert.Equal(ModelFinishReason.Completed, response.FinishReason);
        Assert.Equal("done", response.Message.Text);
        Assert.Equal(3, response.Usage?.TotalTokens);
    }

    [Fact]
    public void ModelResponse_ToolCallsFactory_SetsExplicitReason()
    {
        var response = ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(Call()));

        Assert.Equal(ModelFinishReason.ToolCalls, response.FinishReason);
        Assert.Single(response.Message.ToolCalls);
    }

    [Theory]
    [InlineData(ModelFinishReason.Completed, "empty")]
    [InlineData(ModelFinishReason.Completed, "tools")]
    [InlineData(ModelFinishReason.ToolCalls, "empty")]
    [InlineData(ModelFinishReason.ToolCalls, "text")]
    [InlineData(ModelFinishReason.LengthLimit, "tools")]
    [InlineData(ModelFinishReason.ContentFiltered, "tools")]
    [InlineData(ModelFinishReason.Refused, "tools")]
    [InlineData(ModelFinishReason.Cancelled, "text")]
    [InlineData(ModelFinishReason.Cancelled, "tools")]
    [InlineData(ModelFinishReason.Unknown, "tools")]
    public void ModelResponse_MessageDoesNotMatchReason_RejectsAtBoundary(
        ModelFinishReason finishReason,
        string messageKind)
    {
        var message = messageKind switch
        {
            "text" => AssistantMessage.FromText("partial"),
            "tools" => AssistantMessage.FromToolCalls(Call()),
            _ => new AssistantMessage(null, [])
        };

        Assert.Throws<ArgumentException>(() => new ModelResponse(message, finishReason));
    }

    [Fact]
    public void ModelResponse_UndefinedReason_RejectsAtBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelResponse(new AssistantMessage(null, []), (ModelFinishReason)int.MaxValue));
    }

    [Theory]
    [InlineData(ModelFinishReason.LengthLimit, AgentExecutionErrorCode.ModelOutputIncomplete)]
    [InlineData(ModelFinishReason.ContentFiltered, AgentExecutionErrorCode.ModelOutputFiltered)]
    [InlineData(ModelFinishReason.Refused, AgentExecutionErrorCode.ModelRefused)]
    [InlineData(ModelFinishReason.Unknown, AgentExecutionErrorCode.InvalidModelResponse)]
    public async Task RunAsync_NonSuccessfulTerminalReason_UsesExactErrorCode(
        ModelFinishReason finishReason,
        AgentExecutionErrorCode expectedCode)
    {
        var response = new ModelResponse(AssistantMessage.FromText("partial"), finishReason);

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            Runtime(response).RunAsync(Request()));

        Assert.Equal(expectedCode, exception.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_CancelledReasonWithoutCallerCancellation_IsProviderFailure()
    {
        var response = new ModelResponse(new AssistantMessage(null, []), ModelFinishReason.Cancelled);

        var exception = await Assert.ThrowsAsync<AgentProviderException>(() =>
            Runtime(response).RunAsync(Request()));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task RunAsync_ProviderReturnsCancelledAfterCallerCancellation_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedAgentModelClient([(_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ModelResponse(
                new AssistantMessage(null, []),
                ModelFinishReason.Cancelled));
        }]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AgentRuntime(provider, new AgentToolRegistry([])).RunAsync(Request(), cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_LengthLimitWithoutPartialText_UsesIncompleteCode()
    {
        var response = new ModelResponse(new AssistantMessage(null, []), ModelFinishReason.LengthLimit);

        var exception = await Assert.ThrowsAsync<AgentExecutionException>(() =>
            Runtime(response).RunAsync(Request()));

        Assert.Equal(AgentExecutionErrorCode.ModelOutputIncomplete, exception.ErrorCode);
    }

    private static AgentRuntime Runtime(ModelResponse response) =>
        new(ScriptedAgentModelClient.FromResponses(response), new AgentToolRegistry([]));

    private static AgentRunRequest Request() => new(
        new AgentDefinition(
            new AgentId("finish-reason-agent"),
            "Test finish reasons",
            new ModelId("test-model"),
            [],
            new AgentExecutionLimits()),
        [new UserMessage("hello")]);

    private static ToolCall Call() =>
        new("call-1", "tool", JsonDocument.Parse("{}").RootElement.Clone());
}
