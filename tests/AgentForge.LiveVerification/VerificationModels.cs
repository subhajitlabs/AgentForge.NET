using AgentForge.Abstractions;

namespace AgentForge.LiveVerification;

/// <summary>Outcome of one live verification scenario.</summary>
public enum VerificationStatus { Pass, Fail, Inconclusive, Skipped }

/// <summary>Safe, content-free summary of one scenario.</summary>
public sealed record ScenarioResult(
    string Name,
    VerificationStatus Status,
    int ModelCalls,
    int ToolCalls,
    ModelUsage? Usage,
    string ErrorClassification);

/// <summary>Fixed limits applied by the live verification executable.</summary>
public static class LiveVerificationLimits
{
    /// <summary>Maximum model calls for a text-only scenario.</summary>
    public const int TextModelCalls = 1;
    /// <summary>Maximum model calls for the tool scenario.</summary>
    public const int ToolModelCalls = 2;
    /// <summary>Maximum tool calls for every scenario.</summary>
    public const int MaxToolCalls = 1;
    /// <summary>Per-scenario timeout.</summary>
    public static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(30);
}

/// <summary>Writes only sanitized verification summaries.</summary>
public static class VerificationReporter
{
    /// <summary>Formats a result without prompts, credentials, responses, or exception messages.</summary>
    public static string Format(ScenarioResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var usage = result.Usage is null
            ? "unavailable"
            : $"input={result.Usage.InputTokens}, output={result.Usage.OutputTokens}, total={result.Usage.TotalTokens}";
        return $"{result.Name}: {result.Status}; model calls={result.ModelCalls}; tool calls={result.ToolCalls}; tokens={usage}; error={result.ErrorClassification}";
    }
}
