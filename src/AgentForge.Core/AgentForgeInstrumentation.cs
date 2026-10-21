using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using AgentForge.Abstractions;

namespace AgentForge.Core;

internal static class AgentForgeInstrumentation
{
    internal const string SourceName = "AgentForge";
    internal const string ExecutionActivityName = "agentforge.agent.execute";
    internal const string ModelActivityName = "agentforge.model.invoke";
    internal const string ToolActivityName = "agentforge.tool.invoke";
    internal const string TerminalDiagnosticName = "AgentForge.Execution.Terminal";

    private static readonly string? Version = typeof(AgentForgeInstrumentation).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static readonly ActivitySource Source = new(SourceName, Version);
    private static readonly Meter Meter = new(SourceName, Version);
    private static readonly DiagnosticListener Diagnostics = new(SourceName);
    private static readonly Counter<long> Executions = Meter.CreateCounter<long>(
        "agentforge.agent.executions", "{execution}", "Started agent executions.");
    private static readonly Histogram<double> ExecutionDuration = Meter.CreateHistogram<double>(
        "agentforge.agent.execution.duration", "s", "Terminal agent execution duration.");
    private static readonly Counter<long> ModelInvocations = Meter.CreateCounter<long>(
        "agentforge.model.invocations", "{invocation}", "Started model invocations.");
    private static readonly Histogram<double> ModelInvocationDuration = Meter.CreateHistogram<double>(
        "agentforge.model.invocation.duration", "s", "Terminal model invocation duration.");
    private static readonly Counter<long> ToolInvocations = Meter.CreateCounter<long>(
        "agentforge.tool.invocations", "{invocation}", "Started tool invocations.");
    private static readonly Histogram<double> ToolInvocationDuration = Meter.CreateHistogram<double>(
        "agentforge.tool.invocation.duration", "s", "Terminal tool invocation duration.");
    private static readonly Counter<long> TerminalFailures = Meter.CreateCounter<long>(
        "agentforge.agent.failures", "{failure}", "Terminal failed agent executions.");
    private static readonly Counter<long> ObservedTokens = Meter.CreateCounter<long>(
        "agentforge.model.tokens", "{token}", "Authoritative provider-reported tokens.");

    public static void RecordExecutionStarted(string mode)
    {
        if (!Executions.Enabled)
            return;
        try { Executions.Add(1, Tags("agent_execution", mode)); }
        catch (Exception) { /* Consumer telemetry callbacks cannot affect execution. */ }
    }

    public static long GetTimestamp(TimeProvider timeProvider) => timeProvider.GetTimestamp();

    public static void RecordModelStarted(string mode)
    {
        if (!ModelInvocations.Enabled)
            return;
        try { ModelInvocations.Add(1, Tags("model_turn", mode)); }
        catch (Exception) { /* Consumer telemetry callbacks cannot affect execution. */ }
    }

    public static void RecordToolStarted(string mode)
    {
        if (!ToolInvocations.Enabled)
            return;
        try { ToolInvocations.Add(1, Tags("tool_invocation", mode)); }
        catch (Exception) { /* Consumer telemetry callbacks cannot affect execution. */ }
    }

    public static void RecordModelDuration(
        TimeProvider timeProvider, long startedAt, string mode, Exception? exception = null, bool earlyDisposal = false)
    {
        if (!ModelInvocationDuration.Enabled)
            return;
        try
        {
            ModelInvocationDuration.Record(
                timeProvider.GetElapsedTime(startedAt).TotalSeconds,
                TerminalTags("model_turn", mode, Classify(exception, earlyDisposal)));
        }
        catch (Exception) { /* Consumer telemetry callbacks cannot affect execution. */ }
    }

    public static void RecordToolDuration(
        TimeProvider timeProvider, long startedAt, string mode, Exception? exception = null, bool earlyDisposal = false)
    {
        if (!ToolInvocationDuration.Enabled)
            return;
        try
        {
            ToolInvocationDuration.Record(
                timeProvider.GetElapsedTime(startedAt).TotalSeconds,
                TerminalTags("tool_invocation", mode, Classify(exception, earlyDisposal)));
        }
        catch (Exception) { /* Consumer telemetry callbacks cannot affect execution. */ }
    }

    public static void RecordUsage(ModelUsage usage)
    {
        if (!ObservedTokens.Enabled)
            return;
        try
        {
            ObservedTokens.Add(usage.InputTokens, new KeyValuePair<string, object?>("agentforge.token.type", "input"));
            ObservedTokens.Add(usage.OutputTokens, new KeyValuePair<string, object?>("agentforge.token.type", "output"));
        }
        catch (Exception) { /* Consumer telemetry callbacks cannot affect execution. */ }
    }

    public static void RecordExecutionTerminal(
        TimeProvider timeProvider,
        long startedAt,
        string mode,
        Exception? exception,
        bool earlyDisposal,
        int modelInvocations,
        int toolInvocations,
        long inputTokens,
        long outputTokens,
        bool hasUsage)
    {
        var diagnosticEnabled = Diagnostics.IsEnabled(TerminalDiagnosticName);
        if (!ExecutionDuration.Enabled && !TerminalFailures.Enabled && !diagnosticEnabled)
            return;

        var classification = Classify(exception, earlyDisposal);
        var elapsed = timeProvider.GetElapsedTime(startedAt);
        var tags = TerminalTags("agent_execution", mode, classification);
        if (ExecutionDuration.Enabled)
        {
            try { ExecutionDuration.Record(elapsed.TotalSeconds, tags); }
            catch (Exception) { /* Consumer telemetry callbacks cannot affect execution. */ }
        }
        if (classification.IsFailure && TerminalFailures.Enabled)
        {
            try { TerminalFailures.Add(1, tags); }
            catch (Exception) { /* Consumer telemetry callbacks cannot affect execution. */ }
        }

        if (diagnosticEnabled)
        {
            try
            {
                Diagnostics.Write(TerminalDiagnosticName, new AgentExecutionDiagnostic(
                    mode,
                    classification.Outcome,
                    classification.ErrorCode,
                    classification.ProviderFailureKind,
                    modelInvocations,
                    toolInvocations,
                    hasUsage ? inputTokens : null,
                    hasUsage ? outputTokens : null,
                    elapsed));
            }
            catch (Exception) { /* Consumer diagnostic callbacks cannot affect execution. */ }
        }
    }

    public static Activity? StartExecution(string mode)
    {
        var activity = Source.StartActivity(ExecutionActivityName, ActivityKind.Internal);
        activity?.SetTag("agentforge.operation.type", "agent_execution");
        activity?.SetTag("agentforge.operation.mode", mode);
        return activity;
    }

    public static Activity? StartModelTurn(string mode, int turn)
    {
        var activity = Source.StartActivity(ModelActivityName, ActivityKind.Internal);
        activity?.SetTag("agentforge.operation.type", "model_turn");
        activity?.SetTag("agentforge.operation.mode", mode);
        activity?.SetTag("agentforge.model.turn", turn);
        return activity;
    }

    public static Activity? StartToolInvocation(string mode, int invocation)
    {
        var activity = Source.StartActivity(ToolActivityName, ActivityKind.Internal);
        activity?.SetTag("agentforge.operation.type", "tool_invocation");
        activity?.SetTag("agentforge.operation.mode", mode);
        activity?.SetTag("agentforge.tool.invocation", invocation);
        return activity;
    }

    public static void RecordSuccess(Activity? activity) =>
        activity?.SetTag("agentforge.outcome", "success");

    public static void RecordModelSuccess(Activity? activity, ModelResponse response)
    {
        if (activity is null)
            return;

        activity.SetTag("agentforge.outcome", "success");
        activity.SetTag("agentforge.finish.reason", ToStableValue(response.FinishReason));
        if (response.Usage is { } usage)
        {
            activity.SetTag("agentforge.usage.input_tokens", usage.InputTokens);
            activity.SetTag("agentforge.usage.output_tokens", usage.OutputTokens);
        }
    }

    public static void RecordCallerCancellation(Activity? activity)
    {
        activity?.SetTag("agentforge.outcome", "cancelled");
        activity?.SetTag("agentforge.error.code", "caller_cancelled");
    }

    public static void RecordEarlyDisposal(Activity? activity)
    {
        activity?.SetTag("agentforge.outcome", "cancelled");
        activity?.SetTag("agentforge.error.code", "stream_disposed");
    }

    public static void RecordFailure(Activity? activity, Exception exception)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error);
        switch (exception)
        {
            case AgentExecutionException execution:
                activity.SetTag("agentforge.outcome",
                    execution.ErrorCode == AgentExecutionErrorCode.ExecutionTimedOut ? "timeout" : "rejected");
                activity.SetTag("agentforge.error.code", ToStableValue(execution.ErrorCode));
                SetProviderTags(activity, execution.InnerException);
                break;
            case AgentProviderException provider:
                activity.SetTag("agentforge.outcome", "provider_failed");
                activity.SetTag("agentforge.error.code", "provider_failed");
                SetProviderTags(activity, provider.InnerException);
                break;
            case ModelProviderException provider:
                activity.SetTag("agentforge.outcome", "provider_failed");
                activity.SetTag("agentforge.error.code", "provider_failed");
                SetProviderTags(activity, provider);
                break;
            case AgentToolException:
                activity.SetTag("agentforge.outcome", "tool_failed");
                activity.SetTag("agentforge.error.code", "tool_failed");
                break;
            case OperationCanceledException:
                RecordCallerCancellation(activity);
                activity.SetStatus(ActivityStatusCode.Unset);
                break;
            default:
                activity.SetTag("agentforge.outcome", "error");
                activity.SetTag("agentforge.error.code", "unclassified");
                break;
        }
    }

    private static void SetProviderTags(Activity activity, Exception? exception)
    {
        var provider = FindProviderException(exception);
        if (provider is null)
            return;

        activity.SetTag("agentforge.provider.error.kind", ToStableValue(provider.Kind));
        activity.SetTag("agentforge.provider.is_transient", provider.IsTransient);
    }

    private static ModelProviderException? FindProviderException(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is ModelProviderException provider)
                return provider;
            exception = exception.InnerException;
        }

        return null;
    }

    private static string ToStableValue<T>(T value) where T : struct, Enum =>
        value.ToString().ToLowerInvariant();

    private static TagList Tags(string operationType, string mode) => new()
    {
        { "agentforge.operation.type", operationType },
        { "agentforge.operation.mode", mode }
    };

    private static TagList TerminalTags(string operationType, string mode, Exception? exception) =>
        TerminalTags(operationType, mode, Classify(exception, false));

    private static TagList TerminalTags(string operationType, string mode, TerminalClassification classification)
    {
        var tags = Tags(operationType, mode);
        tags.Add("agentforge.outcome", classification.Outcome);
        if (classification.ErrorCode is not null)
            tags.Add("agentforge.error.code", classification.ErrorCode);
        if (classification.ProviderFailureKind is not null)
            tags.Add("agentforge.provider.error.kind", classification.ProviderFailureKind);
        return tags;
    }

    private static TerminalClassification Classify(Exception? exception, bool earlyDisposal)
    {
        if (earlyDisposal)
            return new("cancelled", "stream_disposed", null, false);
        if (exception is null)
            return new("success", null, null, false);
        if (exception is OperationCanceledException)
            return new("cancelled", "caller_cancelled", null, false);

        var provider = FindProviderException(exception);
        return exception switch
        {
            AgentExecutionException execution => new(
                execution.ErrorCode == AgentExecutionErrorCode.ExecutionTimedOut ? "timeout" : "rejected",
                ToStableValue(execution.ErrorCode),
                provider is null ? null : ToStableValue(provider.Kind),
                true),
            AgentProviderException => new("provider_failed", "provider_failed",
                provider is null ? null : ToStableValue(provider.Kind), true),
            ModelProviderException modelProvider => new("provider_failed", "provider_failed",
                ToStableValue(modelProvider.Kind), true),
            AgentToolException => new("tool_failed", "tool_failed", null, true),
            _ => new("error", "unclassified", null, true)
        };
    }

    private readonly record struct TerminalClassification(
        string Outcome,
        string? ErrorCode,
        string? ProviderFailureKind,
        bool IsFailure);
}
