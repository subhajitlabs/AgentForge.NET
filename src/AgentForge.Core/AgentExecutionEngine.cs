using System.Text.Json;
using System.Text;
using AgentForge.Abstractions;
using Microsoft.Extensions.Logging;

namespace AgentForge.Core;

internal sealed class AgentExecutionEngine
{
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public AgentExecutionEngine(IAgentToolRegistry toolRegistry, ILogger logger, TimeProvider timeProvider)
    {
        _toolRegistry = toolRegistry;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<AgentRunResult> ExecuteAsync(
        AgentRunRequest request,
        Func<ModelRequest, CancellationToken, Task<ModelResponse>> invokeModelAsync,
        string executionMode,
        CancellationToken cancellationToken,
        Func<AgentRunResult, CancellationToken, ValueTask>? publishCompletionAsync = null,
        CancellationToken callerCancellationToken = default)
    {
        AgentForgeInstrumentation.RecordExecutionStarted(executionMode);
        var startedAt = AgentForgeInstrumentation.GetTimestamp(_timeProvider);
        using var executionActivity = AgentForgeInstrumentation.StartExecution(executionMode);
        ExecutionContext? context = null;
        Exception? terminalException = null;
        var earlyDisposal = false;
        try
        {
            ValidateRequest(request);
            ArgumentNullException.ThrowIfNull(invokeModelAsync);
            cancellationToken.ThrowIfCancellationRequested();

            context = CreateContext(request);
            _logger.LogInformation(
                "Agent execution {ExecutionId} started for agent {AgentId} using model {ModelId}",
                context.ExecutionId, request.Agent.Id, request.Agent.Model);

            using var timeoutCancellation = request.Agent.Options.ExecutionTimeout is { } timeout
                ? new CancellationTokenSource(timeout, _timeProvider)
                : null;
            using var linkedCancellation = timeoutCancellation is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
            var executionToken = linkedCancellation?.Token ?? cancellationToken;
            var originalCallerToken = callerCancellationToken;

            try
            {
                while (true)
                {
                    executionToken.ThrowIfCancellationRequested();
                    var response = await InvokeModelAsync(
                        request, context, invokeModelAsync, executionMode, executionToken,
                        originalCallerToken, timeoutCancellation?.Token ?? default).ConfigureAwait(false);
                    ValidateResponse(response, context.ExecutionId);
                    EnforceFinishReason(response.FinishReason, context.ExecutionId, executionToken);
                    EnforceOutputLimit(response.Message.Text, request.Agent.Options.ResourceLimits, context);
                    AccumulateAndEnforceUsage(response.Usage, request.Agent.Options.TokenBudget, context);

                    var validatedBatch = response.Message.ToolCalls.Count == 0
                        ? new ValidatedToolBatch([], context.ToolArgumentBytes)
                        : PrevalidateToolBatch(
                            response.Message.ToolCalls,
                            context.AllowedTools,
                            context.SeenToolCallIds,
                            request.Agent.Limits.MaxToolCalls - context.ToolCalls,
                            request.Agent.Options.ResourceLimits,
                            context.ToolArgumentBytes,
                            context.ExecutionId);
                    context.ToolArgumentBytes = validatedBatch.TotalArgumentBytes;
                    var assistantMessage = CloneAssistantMessage(response.Message);
                    context.Transcript.Add(assistantMessage);

                    if (assistantMessage.ToolCalls.Count == 0)
                    {
                        var result = Complete(context, assistantMessage, executionToken);
                        if (publishCompletionAsync is not null)
                            await publishCompletionAsync(result, executionToken).ConfigureAwait(false);
                        executionToken.ThrowIfCancellationRequested();
                        AgentForgeInstrumentation.RecordSuccess(executionActivity);
                        return result;
                    }

                    await ExecuteToolBatchAsync(
                        validatedBatch.Calls,
                        request.Agent.Options.ResourceLimits,
                        context,
                        executionMode,
                        executionToken,
                        originalCallerToken,
                        timeoutCancellation?.Token ?? default).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                if (originalCallerToken.IsCancellationRequested)
                {
                    AgentForgeInstrumentation.RecordCallerCancellation(executionActivity);
                    originalCallerToken.ThrowIfCancellationRequested();
                }
                if (timeoutCancellation?.IsCancellationRequested == true)
                {
                    var timeoutException = PolicyError(
                        AgentExecutionErrorCode.ExecutionTimedOut,
                        context.ExecutionId,
                        "The agent execution exceeded its configured timeout.");
                    AgentForgeInstrumentation.RecordFailure(executionActivity, timeoutException);
                    throw timeoutException;
                }

                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    earlyDisposal = true;
                    AgentForgeInstrumentation.RecordEarlyDisposal(executionActivity);
                    throw;
                }

                AgentForgeInstrumentation.RecordFailure(executionActivity, exception);
                throw;
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            terminalException = exception;
            if (!callerCancellationToken.IsCancellationRequested)
            {
                earlyDisposal = true;
                AgentForgeInstrumentation.RecordEarlyDisposal(executionActivity);
            }
            else
            {
                AgentForgeInstrumentation.RecordCallerCancellation(executionActivity);
            }
            throw;
        }
        catch (Exception exception)
        {
            terminalException = exception;
            AgentForgeInstrumentation.RecordFailure(executionActivity, exception);
            throw;
        }
        finally
        {
            AgentForgeInstrumentation.RecordExecutionTerminal(
                _timeProvider,
                startedAt,
                executionMode,
                terminalException,
                earlyDisposal,
                context?.ModelInvocations ?? 0,
                context?.ToolInvocations ?? 0,
                context?.InputTokens ?? 0,
                context?.OutputTokens ?? 0,
                context?.HasUsage ?? false);
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

    private async Task<ModelResponse> InvokeModelAsync(
        AgentRunRequest request,
        ExecutionContext context,
        Func<ModelRequest, CancellationToken, Task<ModelResponse>> invokeModelAsync,
        string executionMode,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken,
        CancellationToken timeoutCancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ModelCalls >= request.Agent.Limits.MaxModelCalls)
            throw PolicyError(AgentExecutionErrorCode.ModelCallLimitExceeded, context.ExecutionId, "The model-call limit was reached before completion.");
        EnforceInputLimit(context.Transcript, request.Agent.Options.ResourceLimits, context.ExecutionId);

        var modelRequest = new ModelRequest(
            context.ExecutionId,
            request.Agent.Model,
            context.Transcript.ToArray(),
            context.Descriptors,
            request.Agent.Options.ResourceLimits);

        using var activity = AgentForgeInstrumentation.StartModelTurn(executionMode, context.ModelCalls + 1);
        AgentForgeInstrumentation.RecordModelStarted(executionMode);
        var startedAt = AgentForgeInstrumentation.GetTimestamp(_timeProvider);
        context.ModelInvocations++;
        Exception? terminalException = null;
        var earlyDisposal = false;
        try
        {
            var response = await invokeModelAsync(modelRequest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            context.ModelCalls++;
            AgentForgeInstrumentation.RecordModelSuccess(activity, response);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (callerCancellationToken.IsCancellationRequested || !timeoutCancellationToken.IsCancellationRequested)
            {
                terminalException = new OperationCanceledException();
                earlyDisposal = !callerCancellationToken.IsCancellationRequested;
            }
            else
            {
                terminalException = PolicyError(
                    AgentExecutionErrorCode.ExecutionTimedOut,
                    context.ExecutionId,
                    "The agent execution exceeded its configured timeout.");
            }
            RecordCancellation(activity, context.ExecutionId, callerCancellationToken, timeoutCancellationToken);
            throw;
        }
        catch (ModelTurnRejectedException exception)
        {
            terminalException = PolicyError(exception.ErrorCode, context.ExecutionId, exception.Message);
            AgentForgeInstrumentation.RecordFailure(activity, terminalException);
            throw terminalException;
        }
        catch (ModelProviderException exception)
            when (exception.Kind == ModelProviderErrorKind.ProviderProcessingLimitExceeded)
        {
            var translated = new AgentExecutionException(
                AgentExecutionErrorCode.ProviderProcessingLimitExceeded,
                context.ExecutionId,
                "Provider processing exceeded a configured resource bound.",
                exception);
            terminalException = translated;
            AgentForgeInstrumentation.RecordFailure(activity, translated);
            throw translated;
        }
        catch (ModelProviderException exception)
            when (exception.Kind == ModelProviderErrorKind.TokenUsageUnavailableOrInvalid)
        {
            var translated = new AgentExecutionException(
                AgentExecutionErrorCode.TokenUsageUnavailableOrInvalid,
                context.ExecutionId,
                "The provider returned invalid token usage.",
                exception);
            terminalException = translated;
            AgentForgeInstrumentation.RecordFailure(activity, translated);
            throw translated;
        }
        catch (Exception exception)
        {
            var translated = new AgentProviderException(
                context.ExecutionId,
                $"The model provider failed during execution '{context.ExecutionId}'.",
                exception);
            terminalException = translated;
            AgentForgeInstrumentation.RecordFailure(activity, translated);
            throw translated;
        }
        finally
        {
            AgentForgeInstrumentation.RecordModelDuration(
                _timeProvider, startedAt, executionMode, terminalException, earlyDisposal);
        }
    }

    private static void AccumulateAndEnforceUsage(
        ModelUsage? usage,
        AgentTokenBudget? budget,
        ExecutionContext context)
    {
        if (usage is null)
        {
            if (budget?.IsEnabled == true)
                throw PolicyError(AgentExecutionErrorCode.TokenUsageUnavailableOrInvalid, context.ExecutionId,
                    "Provider-reported token usage is required by the configured token budget.");
            return;
        }

        if (usage.InputTokens < 0 || usage.OutputTokens < 0)
            throw PolicyError(AgentExecutionErrorCode.TokenUsageUnavailableOrInvalid, context.ExecutionId,
                "The provider returned invalid token usage.");

        long inputTokens;
        long outputTokens;
        long totalTokens;
        try
        {
            _ = usage.TotalTokens;
            inputTokens = checked(context.InputTokens + usage.InputTokens);
            outputTokens = checked(context.OutputTokens + usage.OutputTokens);
            totalTokens = checked(inputTokens + outputTokens);
        }
        catch (OverflowException exception)
        {
            throw new AgentExecutionException(
                AgentExecutionErrorCode.TokenUsageUnavailableOrInvalid,
                context.ExecutionId,
                "The provider returned token usage that exceeds supported limits.",
                exception);
        }

        AgentForgeInstrumentation.RecordUsage(usage);

        if (inputTokens > budget?.MaxInputTokens || outputTokens > budget?.MaxOutputTokens ||
            totalTokens > budget?.MaxTotalTokens)
            throw PolicyError(AgentExecutionErrorCode.TokenBudgetExceeded, context.ExecutionId,
                "Observed provider-reported token usage exceeded a configured execution budget.");

        context.InputTokens = inputTokens;
        context.OutputTokens = outputTokens;
        context.HasUsage = true;
    }

    private async Task ExecuteToolBatchAsync(
        IReadOnlyList<ValidatedToolCall> validated,
        AgentResourceLimits? resourceLimits,
        ExecutionContext context,
        string executionMode,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken,
        CancellationToken timeoutCancellationToken)
    {
        foreach (var item in validated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var activity = AgentForgeInstrumentation.StartToolInvocation(executionMode, context.ToolCalls + 1);
            AgentForgeInstrumentation.RecordToolStarted(executionMode);
            var startedAt = AgentForgeInstrumentation.GetTimestamp(_timeProvider);
            context.ToolInvocations++;
            Exception? terminalException = null;
            var earlyDisposal = false;
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
                if (callerCancellationToken.IsCancellationRequested || !timeoutCancellationToken.IsCancellationRequested)
                {
                    terminalException = new OperationCanceledException();
                    earlyDisposal = !callerCancellationToken.IsCancellationRequested;
                }
                else
                {
                    terminalException = PolicyError(
                        AgentExecutionErrorCode.ExecutionTimedOut,
                        context.ExecutionId,
                        "The agent execution exceeded its configured timeout.");
                }
                RecordCancellation(activity, context.ExecutionId, callerCancellationToken, timeoutCancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                var translated = new AgentToolException(
                    context.ExecutionId,
                    item.Call.Name,
                    item.Call.Id,
                    $"A tool failed during execution '{context.ExecutionId}'.",
                    exception);
                terminalException = translated;
                AgentForgeInstrumentation.RecordFailure(activity, translated);
                throw translated;
            }

            try
            {
                if (result is null || result.Value.ValueKind == JsonValueKind.Undefined)
                {
                    throw new AgentToolException(
                        context.ExecutionId,
                        item.Call.Name,
                        item.Call.Id,
                        $"A tool returned an invalid result during execution '{context.ExecutionId}'.",
                        new InvalidOperationException("A tool result must be non-null and contain a valid JSON value."));
                }

                var resultBytes = GetJsonUtf8ByteCount(
                    result.Value,
                    AgentExecutionErrorCode.ToolResultSizeLimitExceeded,
                    context.ExecutionId,
                    "The accumulated tool result payload exceeded its configured UTF-8 byte budget.");
                context.ToolResultBytes = AddAndEnforce(
                    context.ToolResultBytes,
                    resultBytes,
                    resourceLimits?.MaxToolResultBytes,
                    AgentExecutionErrorCode.ToolResultSizeLimitExceeded,
                    context.ExecutionId,
                    "The accumulated tool result payload exceeded its configured UTF-8 byte budget.");
                context.Transcript.Add(new ToolResultMessage(item.Call.Id, item.Call.Name, result.Value));
                context.ToolCalls++;
                AgentForgeInstrumentation.RecordSuccess(activity);
            }
            catch (Exception exception)
            {
                terminalException = exception;
                AgentForgeInstrumentation.RecordFailure(activity, exception);
                throw;
            }
            finally
            {
                AgentForgeInstrumentation.RecordToolDuration(
                    _timeProvider, startedAt, executionMode, terminalException, earlyDisposal);
            }
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
        ArgumentNullException.ThrowIfNull(request.Agent.Options);
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
        if (request.Agent.Options.ExecutionTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "ExecutionTimeout must be positive when configured.");
        ValidateResourceLimits(request.Agent.Options.ResourceLimits, nameof(request));
        ValidateTokenBudget(request.Agent.Options.TokenBudget, nameof(request));
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

    private ValidatedToolBatch PrevalidateToolBatch(
        IReadOnlyList<ToolCall> calls,
        IReadOnlySet<string> allowedTools,
        HashSet<string> seenToolCallIds,
        int remainingBudget,
        AgentResourceLimits? resourceLimits,
        long accumulatedArgumentBytes,
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

        var batchArgumentBytes = 0L;
        foreach (var item in validated)
        {
            var argumentBytes = GetJsonUtf8ByteCount(
                item.Call.Arguments,
                AgentExecutionErrorCode.ToolArgumentSizeLimitExceeded,
                executionId,
                "The accumulated tool argument payload exceeded its configured UTF-8 byte budget.");
            batchArgumentBytes = AddAndEnforce(
                batchArgumentBytes,
                argumentBytes,
                null,
                AgentExecutionErrorCode.ToolArgumentSizeLimitExceeded,
                executionId,
                "The accumulated tool argument payload exceeded its configured UTF-8 byte budget.");
        }
        var totalArgumentBytes = AddAndEnforce(
            accumulatedArgumentBytes, batchArgumentBytes, resourceLimits?.MaxToolArgumentBytes,
            AgentExecutionErrorCode.ToolArgumentSizeLimitExceeded, executionId,
            "The accumulated tool argument payload exceeded its configured UTF-8 byte budget.");

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
        return new ValidatedToolBatch(validated, totalArgumentBytes);
    }

    private static void EnforceInputLimit(
        IReadOnlyList<AgentMessage> transcript,
        AgentResourceLimits? limits,
        ExecutionId executionId)
    {
        if (limits?.MaxInputCharacters is not { } maximum)
            return;

        var count = 0L;
        foreach (var message in transcript)
        {
            var text = message switch
            {
                SystemMessage system => system.Text,
                UserMessage user => user.Text,
                AssistantMessage assistant => assistant.Text,
                _ => null
            };
            if (text is not null)
                count = AddAndEnforce(count, text.Length, maximum, AgentExecutionErrorCode.InputSizeLimitExceeded,
                    executionId, "The model-facing input exceeded its configured character budget.");
        }
    }

    private static void EnforceOutputLimit(
        string? text,
        AgentResourceLimits? limits,
        ExecutionContext context)
    {
        if (text is null)
            return;
        context.OutputCharacters = AddAndEnforce(
            context.OutputCharacters,
            text.Length,
            limits?.MaxOutputCharacters,
            AgentExecutionErrorCode.OutputSizeLimitExceeded,
            context.ExecutionId,
            "The accumulated model output exceeded its configured character budget.");
    }

    private static long GetJsonUtf8ByteCount(
        JsonElement value,
        AgentExecutionErrorCode errorCode,
        ExecutionId executionId,
        string message)
    {
        try
        {
            return Encoding.UTF8.GetByteCount(value.GetRawText());
        }
        catch (OverflowException exception)
        {
            throw new AgentExecutionException(errorCode, executionId, message, exception);
        }
    }

    private static long AddAndEnforce(
        long current,
        long addition,
        long? maximum,
        AgentExecutionErrorCode errorCode,
        ExecutionId executionId,
        string message)
    {
        long total;
        try
        {
            total = checked(current + addition);
        }
        catch (OverflowException exception)
        {
            throw new AgentExecutionException(errorCode, executionId, message, exception);
        }
        if (maximum is { } limit && total > limit)
            throw PolicyError(errorCode, executionId, message);
        return total;
    }

    private static void ValidateResourceLimits(AgentResourceLimits? limits, string parameterName)
    {
        if (limits is null)
            return;
        if (limits.MaxInputCharacters is <= 0 || limits.MaxOutputCharacters is <= 0 ||
            limits.MaxToolArgumentBytes is <= 0 || limits.MaxToolResultBytes is <= 0 ||
            limits.MaxProviderStreamEvents is <= 0 || limits.MaxProviderContentCharacters is <= 0 ||
            limits.MaxProviderToolArgumentBytes is <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Configured resource limits must be positive.");
    }

    private static void ValidateTokenBudget(AgentTokenBudget? budget, string parameterName)
    {
        if (budget is null)
            return;
        if (budget.MaxInputTokens is <= 0 || budget.MaxOutputTokens is <= 0 || budget.MaxTotalTokens is <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Configured token budgets must be positive.");
    }

    private static AssistantMessage CloneAssistantMessage(AssistantMessage message) =>
        new(message.Text, message.ToolCalls.Select(call => new ToolCall(call.Id, call.Name, call.Arguments)));

    private static AgentExecutionException PolicyError(AgentExecutionErrorCode code, ExecutionId executionId, string message) =>
        new(code, executionId, message);

    private static void RecordCancellation(
        System.Diagnostics.Activity? activity,
        ExecutionId executionId,
        CancellationToken callerCancellationToken,
        CancellationToken timeoutCancellationToken)
    {
        if (callerCancellationToken.IsCancellationRequested)
        {
            AgentForgeInstrumentation.RecordCallerCancellation(activity);
            return;
        }

        if (!timeoutCancellationToken.IsCancellationRequested)
        {
            AgentForgeInstrumentation.RecordEarlyDisposal(activity);
            return;
        }

        AgentForgeInstrumentation.RecordFailure(activity, PolicyError(
            AgentExecutionErrorCode.ExecutionTimedOut,
            executionId,
            "The agent execution exceeded its configured timeout."));
    }

    private sealed record ValidatedToolCall(ToolCall Call, IAgentTool Tool);
    private sealed record ValidatedToolBatch(IReadOnlyList<ValidatedToolCall> Calls, long TotalArgumentBytes);

    internal sealed class ModelTurnRejectedException(
        AgentExecutionErrorCode errorCode,
        string message,
        Exception? innerException = null) : Exception(message, innerException)
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
        public int ModelInvocations { get; set; }
        public int ToolInvocations { get; set; }
        public long OutputCharacters { get; set; }
        public long ToolArgumentBytes { get; set; }
        public long ToolResultBytes { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public bool HasUsage { get; set; }
    }
}
