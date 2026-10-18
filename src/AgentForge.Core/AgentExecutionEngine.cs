using System.Text.Json;
using AgentForge.Abstractions;
using Microsoft.Extensions.Logging;

namespace AgentForge.Core;

internal sealed class AgentExecutionEngine
{
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly ILogger _logger;

    public AgentExecutionEngine(IAgentToolRegistry toolRegistry, ILogger logger)
    {
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public async Task<AgentRunResult> ExecuteAsync(
        AgentRunRequest request,
        Func<ModelRequest, CancellationToken, Task<ModelResponse>> invokeModelAsync,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(invokeModelAsync);
        cancellationToken.ThrowIfCancellationRequested();

        var context = CreateContext(request);
        _logger.LogInformation(
            "Agent execution {ExecutionId} started for agent {AgentId} using model {ModelId}",
            context.ExecutionId, request.Agent.Id, request.Agent.Model);

        while (true)
        {
            var response = await InvokeModelAsync(request, context, invokeModelAsync, cancellationToken).ConfigureAwait(false);
            ValidateResponse(response, context.ExecutionId);
            EnforceFinishReason(response.FinishReason, context.ExecutionId, cancellationToken);
            AccumulateUsage(response.Usage, context);

            var validated = response.Message.ToolCalls.Count == 0
                ? Array.Empty<ValidatedToolCall>()
                : PrevalidateToolBatch(
                    response.Message.ToolCalls,
                    context.AllowedTools,
                    context.SeenToolCallIds,
                    request.Agent.Limits.MaxToolCalls - context.ToolCalls,
                    context.ExecutionId);
            var assistantMessage = CloneAssistantMessage(response.Message);
            context.Transcript.Add(assistantMessage);

            if (assistantMessage.ToolCalls.Count == 0)
                return Complete(context, assistantMessage, cancellationToken);

            await ExecuteToolBatchAsync(validated, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private ExecutionContext CreateContext(AgentRunRequest request)
    {
        var executionId = ExecutionId.New();
        var allowedTools = new HashSet<string>(request.Agent.AllowedTools, StringComparer.Ordinal);
        ValidateAllowedTools(allowedTools);
        var descriptors = _toolRegistry.Describe(allowedTools);
        var transcript = new List<AgentMessage>(request.Messages.Count + 4)
        {
            new SystemMessage(request.Agent.Instructions)
        };
        transcript.AddRange(request.Messages);
        return new ExecutionContext(executionId, allowedTools, descriptors, transcript);
    }

    private static async Task<ModelResponse> InvokeModelAsync(
        AgentRunRequest request,
        ExecutionContext context,
        Func<ModelRequest, CancellationToken, Task<ModelResponse>> invokeModelAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ModelCalls >= request.Agent.Limits.MaxModelCalls)
            throw PolicyError(AgentExecutionErrorCode.ModelCallLimitExceeded, context.ExecutionId, "The model-call limit was reached before completion.");

        var modelRequest = new ModelRequest(
            context.ExecutionId,
            request.Agent.Model,
            context.Transcript.ToArray(),
            context.Descriptors);

        try
        {
            var response = await invokeModelAsync(modelRequest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            context.ModelCalls++;
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelTurnRejectedException exception)
        {
            throw PolicyError(exception.ErrorCode, context.ExecutionId, exception.Message);
        }
        catch (Exception exception)
        {
            throw new AgentProviderException(
                context.ExecutionId,
                $"The model provider failed during execution '{context.ExecutionId}'.",
                exception);
        }
    }

    private static void AccumulateUsage(ModelUsage? usage, ExecutionContext context)
    {
        if (usage is null)
            return;

        context.HasUsage = true;
        try
        {
            context.InputTokens = checked(context.InputTokens + usage.InputTokens);
            context.OutputTokens = checked(context.OutputTokens + usage.OutputTokens);
            _ = checked(context.InputTokens + context.OutputTokens);
        }
        catch (OverflowException exception)
        {
            throw new AgentExecutionException(
                AgentExecutionErrorCode.InvalidModelResponse,
                context.ExecutionId,
                "The model response contains usage values that exceed supported limits.",
                exception);
        }
    }

    private static async Task ExecuteToolBatchAsync(
        IReadOnlyList<ValidatedToolCall> validated,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        foreach (var item in validated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToolResult? result;
            try
            {
                result = await item.Tool.InvokeAsync(
                    new ToolInvocation(context.ExecutionId, item.Call.Id, item.Call.Arguments),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AgentToolException(
                    context.ExecutionId,
                    item.Call.Name,
                    item.Call.Id,
                    $"A tool failed during execution '{context.ExecutionId}'.",
                    exception);
            }

            if (result is null || result.Value.ValueKind == JsonValueKind.Undefined)
            {
                throw new AgentToolException(
                    context.ExecutionId,
                    item.Call.Name,
                    item.Call.Id,
                    $"A tool returned an invalid result during execution '{context.ExecutionId}'.",
                    new InvalidOperationException("A tool result must be non-null and contain a valid JSON value."));
            }

            context.Transcript.Add(new ToolResultMessage(item.Call.Id, item.Call.Name, result.Value));
            context.ToolCalls++;
        }
    }

    private AgentRunResult Complete(
        ExecutionContext context,
        AssistantMessage assistantMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "Agent execution {ExecutionId} completed after {ModelCalls} model calls and {ToolCalls} tool calls",
            context.ExecutionId, context.ModelCalls, context.ToolCalls);
        return new AgentRunResult(
            context.ExecutionId,
            assistantMessage,
            context.Transcript.ToArray(),
            context.HasUsage ? new ModelUsage(context.InputTokens, context.OutputTokens) : null,
            context.ModelCalls,
            context.ToolCalls);
    }

    private static void ValidateRequest(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Agent);
        ArgumentNullException.ThrowIfNull(request.Messages);
        ArgumentNullException.ThrowIfNull(request.Agent.AllowedTools);
        ArgumentNullException.ThrowIfNull(request.Agent.Limits);
        if (string.IsNullOrWhiteSpace(request.Agent.Id.Value))
            throw new ArgumentException("Agent ID cannot be empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Agent.Model.Value))
            throw new ArgumentException("Model ID cannot be empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Agent.Instructions))
            throw new ArgumentException("Agent instructions cannot be empty.", nameof(request));
        if (request.Agent.Limits.MaxModelCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxModelCalls must be positive.");
        if (request.Agent.Limits.MaxToolCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxToolCalls must be positive.");
        if (request.Messages.Count == 0 || request.Messages.Any(message => message is not UserMessage user || string.IsNullOrWhiteSpace(user.Text)))
            throw new ArgumentException("Initial messages must contain one or more non-empty user messages.", nameof(request));
    }

    private void ValidateAllowedTools(IReadOnlySet<string> allowedTools)
    {
        foreach (var name in allowedTools)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Allowed tool names cannot be empty.", nameof(allowedTools));
            if (!_toolRegistry.TryResolve(name, out _))
                throw new ArgumentException("Every allowed tool must be registered.", nameof(allowedTools));
        }
    }

    private static void ValidateResponse(ModelResponse? response, ExecutionId executionId)
    {
        if (response?.Message is null)
            throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "The model response must contain one assistant message.");
        if (response.Message.ToolCalls is null)
            throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "The assistant tool-call collection cannot be null.");

        var hasText = !string.IsNullOrWhiteSpace(response.Message.Text);
        var hasToolCalls = response.Message.ToolCalls is { Count: > 0 };
        var invalidCombination = response.FinishReason switch
        {
            ModelFinishReason.Completed => !hasText || hasToolCalls,
            ModelFinishReason.ToolCalls => hasText || !hasToolCalls,
            ModelFinishReason.LengthLimit or ModelFinishReason.ContentFiltered or ModelFinishReason.Refused => hasToolCalls,
            ModelFinishReason.Cancelled => hasText || hasToolCalls,
            ModelFinishReason.Unknown => hasToolCalls,
            _ => true
        };
        if (invalidCombination)
            throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "The assistant message is incompatible with the model finish reason.");
        if (response.Usage is { InputTokens: < 0 } or { OutputTokens: < 0 })
            throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "Model usage values cannot be negative.");
    }

    private static void EnforceFinishReason(ModelFinishReason finishReason, ExecutionId executionId, CancellationToken cancellationToken)
    {
        switch (finishReason)
        {
            case ModelFinishReason.Completed:
            case ModelFinishReason.ToolCalls:
                return;
            case ModelFinishReason.LengthLimit:
                throw PolicyError(AgentExecutionErrorCode.ModelOutputIncomplete, executionId, "The model output ended before completion.");
            case ModelFinishReason.ContentFiltered:
                throw PolicyError(AgentExecutionErrorCode.ModelOutputFiltered, executionId, "The model output was filtered.");
            case ModelFinishReason.Refused:
                throw PolicyError(AgentExecutionErrorCode.ModelRefused, executionId, "The model refused the request.");
            case ModelFinishReason.Cancelled:
                cancellationToken.ThrowIfCancellationRequested();
                throw new AgentProviderException(
                    executionId,
                    $"The model provider reported cancellation during execution '{executionId}'.",
                    new InvalidOperationException("The provider reported cancellation without caller cancellation."));
            case ModelFinishReason.Unknown:
            default:
                throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "The model returned an unsupported finish reason.");
        }
    }

    private IReadOnlyList<ValidatedToolCall> PrevalidateToolBatch(
        IReadOnlyList<ToolCall> calls,
        IReadOnlySet<string> allowedTools,
        HashSet<string> seenToolCallIds,
        int remainingBudget,
        ExecutionId executionId)
    {
        var callIds = new HashSet<string>(seenToolCallIds, StringComparer.Ordinal);
        var validated = new List<ValidatedToolCall>(calls.Count);
        foreach (var call in calls)
        {
            if (call is null || string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name) || call.Arguments.ValueKind == JsonValueKind.Undefined)
                throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "Every tool call requires an ID, name, and valid JSON arguments.");
            if (!callIds.Add(call.Id))
                throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "The model response contains a duplicate tool-call identifier.");
            if (!allowedTools.Contains(call.Name))
                throw PolicyError(AgentExecutionErrorCode.ToolNotAllowed, executionId, "The model requested a tool that is not allowed for this agent.");
            if (!_toolRegistry.TryResolve(call.Name, out var tool) || tool is null)
                throw PolicyError(AgentExecutionErrorCode.ToolNotFound, executionId, "The model requested a tool that is not registered.");
            validated.Add(new ValidatedToolCall(call, tool));
        }

        if (calls.Count > remainingBudget)
            throw PolicyError(AgentExecutionErrorCode.ToolCallLimitExceeded, executionId, "The requested tool batch exceeds the remaining tool-call budget.");

        foreach (var item in validated)
        {
            if (item.Tool is not IAgentToolArgumentValidator validator)
                continue;

            try
            {
                validator.ValidateArguments(item.Call.Arguments);
            }
            catch (ArgumentException exception)
            {
                throw new AgentExecutionException(
                    AgentExecutionErrorCode.InvalidToolArguments,
                    executionId,
                    "A tool call contains invalid arguments.",
                    exception);
            }
        }

        seenToolCallIds.UnionWith(calls.Select(call => call.Id));
        return validated;
    }

    private static AssistantMessage CloneAssistantMessage(AssistantMessage message) =>
        new(message.Text, message.ToolCalls.Select(call => new ToolCall(call.Id, call.Name, call.Arguments)));

    private static AgentExecutionException PolicyError(AgentExecutionErrorCode code, ExecutionId executionId, string message) =>
        new(code, executionId, message);

    private sealed record ValidatedToolCall(ToolCall Call, IAgentTool Tool);

    internal sealed class ModelTurnRejectedException(
        AgentExecutionErrorCode errorCode,
        string message) : Exception(message)
    {
        public AgentExecutionErrorCode ErrorCode { get; } = errorCode;
    }

    private sealed class ExecutionContext(
        ExecutionId executionId,
        IReadOnlySet<string> allowedTools,
        IReadOnlyList<ToolDescriptor> descriptors,
        List<AgentMessage> transcript)
    {
        public ExecutionId ExecutionId { get; } = executionId;
        public IReadOnlySet<string> AllowedTools { get; } = allowedTools;
        public IReadOnlyList<ToolDescriptor> Descriptors { get; } = descriptors;
        public List<AgentMessage> Transcript { get; } = transcript;
        public HashSet<string> SeenToolCallIds { get; } = new(StringComparer.Ordinal);
        public int ModelCalls { get; set; }
        public int ToolCalls { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public bool HasUsage { get; set; }
    }
}
