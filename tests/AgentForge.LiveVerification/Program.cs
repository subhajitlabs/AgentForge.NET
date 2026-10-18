using AgentForge.Abstractions;
using AgentForge.Hosting;
using AgentForge.Providers.OpenAI;
using AgentForge.Tools;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace AgentForge.LiveVerification;

internal static class Program
{
    private const string CalculatorToolName = "calculator_add";

    public static async Task<int> Main(string[] args)
    {
        var settings = LiveVerificationGate.Evaluate(args, Environment.GetEnvironmentVariable);
        if (!settings.IsEnabled)
        {
            Console.WriteLine($"Live verification skipped: {settings.Message}");
            Console.WriteLine("No provider was constructed and no HTTP request was made.");
            return 0;
        }

        Console.WriteLine("Live verification plan: 4 sequential scenarios; model-call ceilings: 1, 1, 2, 1; tool-call ceiling: 1 each; timeout: 30 seconds each.");
        Console.WriteLine("The current public provider adapter does not expose a maximum-output-token setting. API usage may incur cost.");

        var toolInvocations = 0;
        var unauthorizedToolInvocations = 0;
        var calculator = AgentTool.Create<AddArguments, AddResult>(
            CalculatorToolName,
            "Adds two integer values. Always use this tool for requested arithmetic.",
            (arguments, _, _) =>
            {
                Interlocked.Increment(ref toolInvocations);
                return ValueTask.FromResult(new AddResult(arguments.Left + arguments.Right));
            });
        var unauthorizedTool = AgentTool.Create<AddArguments, AddResult>(
            "calculator.unauthorized",
            "A verification sentinel that must never be available to an agent.",
            (arguments, _, _) =>
            {
                Interlocked.Increment(ref unauthorizedToolInvocations);
                return ValueTask.FromResult(new AddResult(arguments.Left + arguments.Right));
            });

        await using var provider = new ServiceCollection()
            .AddOpenAIModelClient(options => options.ApiKey = settings.ApiKey)
            .AddAgentTool(calculator)
            .AddAgentTool(unauthorizedTool)
            .AddAgentForge()
            .AddAgentDefinition("live-buffered", agent => agent
                .WithInstructions("Follow the user's short instruction exactly and answer concisely.")
                .UseModel(settings.Model!)
                .WithLimits(LiveVerificationLimits.TextModelCalls, LiveVerificationLimits.MaxToolCalls))
            .AddAgentDefinition("live-streaming", agent => agent
                .WithInstructions("Follow the user's short instruction exactly and answer concisely.")
                .UseModel(settings.Model!)
                .WithLimits(LiveVerificationLimits.TextModelCalls, LiveVerificationLimits.MaxToolCalls))
            .AddAgentDefinition("live-tool", agent => agent
                .WithInstructions("You must use calculator_add for all arithmetic. Do not calculate mentally.")
                .UseModel(settings.Model!)
                .AllowTool(CalculatorToolName)
                .WithLimits(LiveVerificationLimits.ToolModelCalls, LiveVerificationLimits.MaxToolCalls))
            .AddAgentDefinition("live-cancellation", agent => agent
                .WithInstructions("Answer concisely.")
                .UseModel(settings.Model!)
                .WithLimits(LiveVerificationLimits.TextModelCalls, LiveVerificationLimits.MaxToolCalls))
            .BuildServiceProvider();

        var results = new List<ScenarioResult>(4)
        {
            await RunBufferedAsync(provider),
            await RunStreamingAsync(provider),
            await RunToolAsync(provider, () => Volatile.Read(ref toolInvocations), () => Volatile.Read(ref unauthorizedToolInvocations)),
            await RunCancellationAsync(provider)
        };

        Console.WriteLine("Live verification report:");
        foreach (var result in results)
            Console.WriteLine(VerificationReporter.Format(result));

        return results.Any(result => result.Status is VerificationStatus.Fail or VerificationStatus.Inconclusive) ? 1 : 0;
    }

    private static async Task<ScenarioResult> RunBufferedAsync(IServiceProvider provider)
    {
        const string name = "Buffered completion";
        try
        {
            using var timeout = new CancellationTokenSource(LiveVerificationLimits.ScenarioTimeout);
            var result = await provider.GetRequiredKeyedService<IAgent>("live-buffered")
                .RunAsync("Reply with a brief greeting.", timeout.Token);
            var passed = !string.IsNullOrWhiteSpace(result.FinalMessage.Text)
                && result.ModelCalls is > 0 and <= LiveVerificationLimits.TextModelCalls
                && result.ToolCalls == 0 && HasValidUsage(result.Usage);
            return Result(name, passed, result, passed ? "none" : "validation");
        }
        catch (Exception exception) { return Failure(name, exception); }
    }

    private static async Task<ScenarioResult> RunStreamingAsync(IServiceProvider provider)
    {
        const string name = "Streaming completion";
        try
        {
            using var timeout = new CancellationTokenSource(LiveVerificationLimits.ScenarioTimeout);
            var deltas = 0;
            AgentRunResult? final = null;
            var completions = 0;
            await foreach (var update in provider.GetRequiredKeyedService<IAgent>("live-streaming")
                .StreamAsync("Reply with a brief greeting.", timeout.Token))
            {
                if (update is AssistantTextDelta delta && !string.IsNullOrEmpty(delta.Text)) deltas++;
                if (update is AgentRunCompleted completed) { completions++; final = completed.Result; }
            }

            var passed = deltas > 0 && completions == 1 && final is not null
                && !string.IsNullOrWhiteSpace(final.FinalMessage.Text)
                && final.ModelCalls is > 0 and <= LiveVerificationLimits.TextModelCalls
                && final.ToolCalls == 0 && HasValidUsage(final.Usage);
            return final is null
                ? new(name, VerificationStatus.Fail, 0, 0, null, "validation")
                : Result(name, passed, final, passed ? "none" : "validation");
        }
        catch (Exception exception) { return Failure(name, exception); }
    }

    private static async Task<ScenarioResult> RunToolAsync(
        IServiceProvider provider,
        Func<int> invocationCount,
        Func<int> unauthorizedInvocationCount)
    {
        const string name = "Typed tool round trip";
        try
        {
            using var timeout = new CancellationTokenSource(LiveVerificationLimits.ScenarioTimeout);
            var result = await provider.GetRequiredKeyedService<IAgent>("live-tool")
                .RunAsync("Use calculator_add to add 20 and 22, then report the numeric result.", timeout.Token);
            if (invocationCount() == 0)
                return new(name, VerificationStatus.Inconclusive, result.ModelCalls, result.ToolCalls, result.Usage, "tool-not-selected");
            var passed = result.ToolCalls == 1
                && result.ModelCalls is > 1 and <= LiveVerificationLimits.ToolModelCalls
                && result.FinalMessage.Text?.Contains("42", StringComparison.Ordinal) == true
                && unauthorizedInvocationCount() == 0 && HasValidUsage(result.Usage);
            return Result(name, passed, result, passed ? "none" : "validation");
        }
        catch (Exception exception) { return Failure(name, exception); }
    }

    private static async Task<ScenarioResult> RunCancellationAsync(IServiceProvider provider)
    {
        const string name = "Caller cancellation";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = await provider.GetRequiredKeyedService<IAgent>("live-cancellation")
                .RunAsync("Reply briefly.", cancellation.Token);
            return new(name, VerificationStatus.Fail, 0, 0, null, "unexpected-success");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new(name, VerificationStatus.Pass, 0, 0, null, "cancelled");
        }
        catch (Exception exception) { return Failure(name, exception); }
    }

    private static ScenarioResult Result(string name, bool passed, AgentRunResult result, string classification) =>
        new(name, passed ? VerificationStatus.Pass : VerificationStatus.Fail,
            result.ModelCalls, result.ToolCalls, result.Usage, classification);

    private static ScenarioResult Failure(string name, Exception exception) =>
        new(name, VerificationStatus.Fail, 0, 0, null, Classify(exception));

    private static bool HasValidUsage(ModelUsage? usage) =>
        usage is null || usage.InputTokens >= 0 && usage.OutputTokens >= 0;

    private static string Classify(Exception exception) => exception switch
    {
        OperationCanceledException => "timeout-or-cancellation",
        AgentProviderException { InnerException: ModelProviderException provider } =>
            $"provider-{provider.Kind.ToString().ToLowerInvariant()}",
        AgentProviderException => "provider",
        AgentExecutionException => "execution",
        AgentToolException => "tool",
        ArgumentException or InvalidOperationException => "configuration",
        _ => "unexpected"
    };

    private sealed record AddArguments(int Left, int Right);
    private sealed record AddResult(int Value);
}
