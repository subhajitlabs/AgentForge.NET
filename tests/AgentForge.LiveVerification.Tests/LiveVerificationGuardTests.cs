using AgentForge.LiveVerification;

namespace AgentForge.LiveVerification.Tests;

public sealed class LiveVerificationGuardTests
{
    [Fact]
    public void Evaluate_NoInputs_GateIsClosed() => Assert.False(Evaluate([]).IsEnabled);

    [Fact]
    public void Evaluate_MissingFlag_GateIsClosed() =>
        Assert.Contains("--live", Evaluate([], CompleteEnvironment()).Message, StringComparison.Ordinal);

    [Fact]
    public void Evaluate_MissingEnvironmentOptIn_GateIsClosed() =>
        Assert.Contains("AGENTFORGE_RUN_LIVE_TESTS", Evaluate(["--live"], CompleteEnvironment(exclude: "AGENTFORGE_RUN_LIVE_TESTS")).Message, StringComparison.Ordinal);

    [Fact]
    public void Evaluate_MissingApiKey_GateIsClosed() =>
        Assert.Contains("OPENAI_API_KEY", Evaluate(["--live"], CompleteEnvironment(exclude: "OPENAI_API_KEY")).Message, StringComparison.Ordinal);

    [Fact]
    public void Evaluate_MissingModel_GateIsClosed() =>
        Assert.Contains("OPENAI_MODEL", Evaluate(["--live"], CompleteEnvironment(exclude: "OPENAI_MODEL")).Message, StringComparison.Ordinal);

    [Fact]
    public void Evaluate_ClosedGate_DoesNotReadPastFirstFailure()
    {
        var reads = 0;
        var result = LiveVerificationGate.Evaluate([], _ => { reads++; return "secret"; });
        Assert.False(result.IsEnabled);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Evaluate_WhitespaceCredential_GateIsClosed() =>
        Assert.False(Evaluate(["--live"], CompleteEnvironment("OPENAI_API_KEY", " ")).IsEnabled);

    [Fact]
    public void Evaluate_InvalidConfiguration_MessageIsSanitized()
    {
        var result = Evaluate(["--live"], CompleteEnvironment(exclude: "OPENAI_MODEL"));
        Assert.DoesNotContain("sentinel-api-key", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel-model", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AllRequirements_SettingsAreEnabled() =>
        Assert.True(Evaluate(["--live"], CompleteEnvironment()).IsEnabled);

    [Fact]
    public void Limits_AreStrictAndBounded()
    {
        Assert.Equal(1, LiveVerificationLimits.TextModelCalls);
        Assert.Equal(2, LiveVerificationLimits.ToolModelCalls);
        Assert.Equal(1, LiveVerificationLimits.MaxToolCalls);
        Assert.InRange(LiveVerificationLimits.ScenarioTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Format_Result_DoesNotRevealCredentialOrResponseContent()
    {
        const string secret = "sentinel-api-key";
        var result = new ScenarioResult("Buffered", VerificationStatus.Fail, 0, 0, null, "provider");
        var output = VerificationReporter.Format(result);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_IsExecutableAndNotATestProject()
    {
        var project = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AgentForge.LiveVerification", "AgentForge.LiveVerification.csproj"));
        Assert.Contains("<OutputType>Exe</OutputType>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.NET.Test.Sdk", project, StringComparison.Ordinal);
    }

    private static LiveVerificationSettings Evaluate(string[] args, IReadOnlyDictionary<string, string>? environment = null) =>
        LiveVerificationGate.Evaluate(args, name => environment is not null && environment.TryGetValue(name, out var value) ? value : null);

    private static IReadOnlyDictionary<string, string> CompleteEnvironment(string? replace = null, string? value = null, string? exclude = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTFORGE_RUN_LIVE_TESTS"] = "true", ["OPENAI_API_KEY"] = "sentinel-api-key", ["OPENAI_MODEL"] = "sentinel-model"
        };
        if (exclude is not null) environment.Remove(exclude);
        if (replace is not null) environment[replace] = value!;
        return environment;
    }
}
