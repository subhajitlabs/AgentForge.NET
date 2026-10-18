namespace AgentForge.LiveVerification;

/// <summary>Evaluates the explicit safeguards required before live provider verification.</summary>
public static class LiveVerificationGate
{
    /// <summary>Checks command-line and environment inputs without constructing a provider.</summary>
    public static LiveVerificationSettings Evaluate(
        IEnumerable<string> arguments,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (!arguments.Contains("--live", StringComparer.Ordinal))
            return LiveVerificationSettings.Closed("The --live flag is required.");
        if (!string.Equals(getEnvironmentVariable("AGENTFORGE_RUN_LIVE_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            return LiveVerificationSettings.Closed("AGENTFORGE_RUN_LIVE_TESTS must be set to true.");

        var apiKey = getEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return LiveVerificationSettings.Closed("OPENAI_API_KEY must be set to a nonempty value.");

        var model = getEnvironmentVariable("OPENAI_MODEL");
        if (string.IsNullOrWhiteSpace(model))
            return LiveVerificationSettings.Closed("OPENAI_MODEL must be set to a nonempty value.");

        return LiveVerificationSettings.Open(apiKey, model);
    }
}

/// <summary>Contains either safely validated live settings or a sanitized closed-gate reason.</summary>
public sealed record LiveVerificationSettings
{
    private LiveVerificationSettings(bool isEnabled, string message, string? apiKey, string? model)
    {
        IsEnabled = isEnabled;
        Message = message;
        ApiKey = apiKey;
        Model = model;
    }

    /// <summary>Gets whether all live-verification gates are open.</summary>
    public bool IsEnabled { get; }
    /// <summary>Gets a safe status message.</summary>
    public string Message { get; }
    /// <summary>Gets the API key only when enabled.</summary>
    public string? ApiKey { get; }
    /// <summary>Gets the model only when enabled.</summary>
    public string? Model { get; }

    internal static LiveVerificationSettings Closed(string message) => new(false, message, null, null);
    internal static LiveVerificationSettings Open(string apiKey, string model) =>
        new(true, "All explicit live-verification gates are open.", apiKey, model);
}
