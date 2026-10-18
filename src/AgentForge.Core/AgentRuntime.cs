using System.Text.Json;
using AgentForge.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.Core;

/// <summary>Default deterministic single-agent runtime.</summary>
public sealed class AgentRuntime : IAgentRuntime
{
    private readonly IAgentModelClient _modelClient;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly ILogger<AgentRuntime> _logger;

    /// <summary>Creates the runtime.</summary>
    /// <param name="modelClient">The provider-neutral model client.</param>
    /// <param name="toolRegistry">The trusted tool registry.</param>
    /// <param name="logger">An optional metadata-only logger.</param>
    public AgentRuntime(
        IAgentModelClient modelClient,
        IAgentToolRegistry toolRegistry,
        ILogger<AgentRuntime>? logger = null)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _logger = logger ?? NullLogger<AgentRuntime>.Instance;
    }

    /// <inheritdoc />
    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        var executionId = ExecutionId.New();
        var allowedTools = new HashSet<string>(request.Agent.AllowedTools, StringComparer.Ordinal);
        ValidateAllowedToolNames(allowedTools);
        var descriptors = _toolRegistry.Describe(allowedTools).ToArray();
        var transcript = new List<AgentMessage>(request.Messages.Count + 4)
        {
            new SystemMessage(request.Agent.Instructions)
        };
        transcript.AddRange(request.Messages);

        var modelCalls = 0;
        var toolCalls = 0;
        long inputTokens = 0;
        long outputTokens = 0;
        var hasUsage = false;

        _logger.LogInformation(
            "Agent execution {ExecutionId} started for agent {AgentId} using model {ModelId}",
            executionId, request.Agent.Id, request.Agent.Model);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (modelCalls >= request.Agent.Limits.MaxModelCalls)
            {
                throw PolicyError(
                    AgentExecutionErrorCode.ModelCallLimitExceeded,
                    executionId,
                    "The model-call limit was reached before completion.");
            }

            var modelRequest = new ModelRequest(
                executionId,
                request.Agent.Model,
                transcript.ToArray(),
                descriptors);

            ModelResponse response;
            try
            {
                response = await _modelClient.CompleteAsync(modelRequest, cancellationToken).ConfigureAwait(false);
                modelCalls++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AgentProviderException(
                    executionId,
                    $"The model provider failed during execution '{executionId}'.",
                    exception);
            }

            ValidateResponse(response, executionId);
            if (response.Usage is not null)
            {
                hasUsage = true;
                inputTokens += response.Usage.InputTokens;
                outputTokens += response.Usage.OutputTokens;
            }

            var validated = response.Message.ToolCalls.Count == 0
                ? Array.Empty<ValidatedToolCall>()
                : PrevalidateToolBatch(
                    response.Message.ToolCalls,
                    allowedTools,
                    request.Agent.Limits.MaxToolCalls - toolCalls,
                    executionId);
            var assistantMessage = CloneAssistantMessage(response.Message);
            transcript.Add(assistantMessage);

            if (assistantMessage.ToolCalls.Count == 0)
            {
                _logger.LogInformation(
                    "Agent execution {ExecutionId} completed after {ModelCalls} model calls and {ToolCalls} tool calls",
                    executionId, modelCalls, toolCalls);
                return new AgentRunResult(
                    executionId,
                    assistantMessage,
                    transcript.ToArray(),
                    hasUsage ? new ModelUsage(inputTokens, outputTokens) : null,
                    modelCalls,
                    toolCalls);
            }

            foreach (var item in validated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ToolResult result;
                try
                {
                    result = await item.Tool.InvokeAsync(
                        new ToolInvocation(executionId, item.Call.Id, item.Call.Arguments.Clone()),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new AgentToolException(
                        executionId,
                        item.Call.Name,
                        item.Call.Id,
                        $"Tool '{item.Call.Name}' failed during execution '{executionId}'.",
                        exception);
                }

                if (result.Value.ValueKind == JsonValueKind.Undefined)
                {
                    throw new AgentToolException(
                        executionId,
                        item.Call.Name,
                        item.Call.Id,
                        $"Tool '{item.Call.Name}' returned an invalid JSON result.",
                        new InvalidOperationException("A tool result cannot be an undefined JSON value."));
                }

                transcript.Add(new ToolResultMessage(item.Call.Id, item.Call.Name, result.Value.Clone()));
                toolCalls++;
            }
        }
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

    private static void ValidateAllowedToolNames(IReadOnlySet<string> allowedTools)
    {
        foreach (var name in allowedTools)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Allowed tool names cannot be empty.", nameof(allowedTools));
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
        if (hasText == hasToolCalls)
            throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "An assistant message must contain either text or tool calls, but not both.");
        if (response.Usage is { InputTokens: < 0 } or { OutputTokens: < 0 })
            throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "Model usage values cannot be negative.");
    }

    private IReadOnlyList<ValidatedToolCall> PrevalidateToolBatch(
        IReadOnlyList<ToolCall> calls,
        IReadOnlySet<string> allowedTools,
        int remainingBudget,
        ExecutionId executionId)
    {
        if (calls.Count > remainingBudget)
            throw PolicyError(AgentExecutionErrorCode.ToolCallLimitExceeded, executionId, "The requested tool batch exceeds the remaining tool-call budget.");

        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<ValidatedToolCall>(calls.Count);
        foreach (var call in calls)
        {
            if (call is null || string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name) || call.Arguments.ValueKind == JsonValueKind.Undefined)
                throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, "Every tool call requires an ID, name, and valid JSON arguments.");
            if (!callIds.Add(call.Id))
                throw PolicyError(AgentExecutionErrorCode.InvalidModelResponse, executionId, $"Duplicate tool-call ID '{call.Id}' was returned in one batch.");
            if (!allowedTools.Contains(call.Name))
                throw PolicyError(AgentExecutionErrorCode.ToolNotAllowed, executionId, $"Tool '{call.Name}' is not allowed for this agent.");
            if (!_toolRegistry.TryResolve(call.Name, out var tool) || tool is null)
                throw PolicyError(AgentExecutionErrorCode.ToolNotFound, executionId, $"Tool '{call.Name}' is not registered.");
            validated.Add(new ValidatedToolCall(call, tool));
        }

        return validated;
    }

    private static AssistantMessage CloneAssistantMessage(AssistantMessage message) =>
        new(message.Text, message.ToolCalls.Select(call => new ToolCall(call.Id, call.Name, call.Arguments.Clone())).ToArray());

    private static AgentExecutionException PolicyError(
        AgentExecutionErrorCode code,
        ExecutionId executionId,
        string message) => new(code, executionId, message);

    private sealed record ValidatedToolCall(ToolCall Call, IAgentTool Tool);
}
