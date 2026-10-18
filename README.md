# AgentForge.NET

AgentForge.NET is an in-process, provider-neutral AI agent runtime for .NET 10. It provides bounded buffered and streaming execution, explicit tool authorization, typed tools, OpenAI and Microsoft.Extensions.AI adapters, and named agents through keyed dependency injection.

## Package responsibilities

- `AgentForge.Abstractions`: provider-neutral contracts and immutable domain values.
- `AgentForge.Core`: execution lifecycle and deterministic scripted clients.
- `AgentForge.Hosting`: DI, named definitions, and the thin `IAgent` facade.
- `AgentForge.Tools`: typed delegate tools and bounded JSON validation.
- `AgentForge.Providers.OpenAI`: official OpenAI Responses API adapter.
- `AgentForge.Adapters.MicrosoftExtensionsAI`: adapter for a caller-owned `IChatClient`.

## Build and test

Install the SDK pinned by `global.json`, then run:

```powershell
dotnet build AgentForge.slnx --configuration Release
dotnet test AgentForge.slnx --configuration Release --no-build
```

## Install from NuGet

All packages in this preview use version `0.2.0-preview.1`. Install the packages needed by the scenario:

```powershell
# Offline scripted model, runtime, named-agent hosting, and typed tools
dotnet add package AgentForge.Hosting --version 0.2.0-preview.1
dotnet add package AgentForge.Tools --version 0.2.0-preview.1

# Add one provider adapter
dotnet add package AgentForge.Providers.OpenAI --version 0.2.0-preview.1
# or
dotnet add package AgentForge.Adapters.MicrosoftExtensionsAI --version 0.2.0-preview.1
```

`AgentForge.Hosting` transitively supplies `AgentForge.Core` and `AgentForge.Abstractions`. Install `AgentForge.Tools` only when using typed tools. Applications that need contracts without the runtime can reference `AgentForge.Abstractions` directly.

## Complete offline quick start

Create a .NET 10 console application, install `AgentForge.Hosting`, and replace `Program.cs` with this complete credential-free example:

```csharp
using AgentForge.Abstractions;
using AgentForge.Core;
using AgentForge.Hosting;
using Microsoft.Extensions.DependencyInjection;

var model = new ScriptedAgentModelClient(
[
    (_, _) => Task.FromResult(ModelResponse.Completed(
        AssistantMessage.FromText("Reset instructions are available on the account page.")))
]);

await using var provider = new ServiceCollection()
    .AddSingleton<IAgentModelClient>(model)
    .AddAgentForge()
    .AddAgentDefinition("support", agent => agent
        .WithInstructions("Answer concisely.")
        .UseModel("offline-script")
        .WithLimits(maxModelCalls: 1, maxToolCalls: 1))
    .BuildServiceProvider();

var agent = provider.GetRequiredKeyedService<IAgent>("support");
var result = await agent.RunAsync("How can I reset my password?");
Console.WriteLine(result.FinalMessage.Text);
```

Each call creates a fresh run and preserves the configured model/tool limits. There is no unkeyed default agent. Duplicate agent keys, missing definitions, unknown allowed tools, and multiple model clients fail deterministically before a model invocation.

## Typed tools and authorization

Install both `AgentForge.Hosting` and `AgentForge.Tools`. The following registration can replace the service-provider construction in the offline quick start:

```csharp
using AgentForge.Tools;

await using var provider = new ServiceCollection()
    .AddSingleton<IAgentModelClient>(model)
    .AddAgentTool(AgentTool.Create<AddArguments, AddResult>(
        "calculator_add",
        "Adds two numbers.",
        static (args, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AddResult(args.Left + args.Right));
        }))
    .AddAgentForge()
    .AddAgentDefinition("calculator", agent => agent
        .WithInstructions("Use the calculator for arithmetic.")
        .UseModel("configured-model")
        .AllowTool("calculator_add")
        .WithLimits(maxModelCalls: 3, maxToolCalls: 2))
    .BuildServiceProvider();

internal sealed record AddArguments(decimal Left, decimal Right);
internal sealed record AddResult(decimal Value);
```

Registration makes a tool available to the application; only `AllowTool` authorizes it for that agent. Arguments remain untrusted and typed validation occurs atomically before any tool in a requested batch executes.

## Streaming

The registered model client must implement `IStreamingAgentModelClient`. The OpenAI and Microsoft.Extensions.AI adapters do; for credential-free development use `ScriptedStreamingAgentModelClient` from `AgentForge.Core`:

```csharp
var model = ScriptedStreamingAgentModelClient.FromEvents(
[
    new ModelTextDelta("Hello"),
    new ModelResponseCompleted(ModelResponse.Completed(
        AssistantMessage.FromText("Hello")))
]);

await foreach (var update in agent.StreamAsync("Hello"))
{
    if (update is AssistantTextDelta delta)
        Console.Write(delta.Text);
    else if (update is AgentRunCompleted completed)
        Console.WriteLine($"Model calls: {completed.Result.ModelCalls}");
}
```

A non-streaming provider fails with `CapabilityNotSupported`; AgentForge never silently switches modes. Cancellation is propagated to the runtime, provider, and tools.

## Providers

For OpenAI, install `AgentForge.Hosting` and `AgentForge.Providers.OpenAI`, set `OPENAI_API_KEY` and the deployment-specific `OPENAI_MODEL`, and never commit credentials:

```csharp
using AgentForge.Abstractions;
using AgentForge.Hosting;
using AgentForge.Providers.OpenAI;
using Microsoft.Extensions.DependencyInjection;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("OPENAI_API_KEY is required.");
var model = Environment.GetEnvironmentVariable("OPENAI_MODEL")
    ?? throw new InvalidOperationException("OPENAI_MODEL is required.");

await using var provider = new ServiceCollection()
    .AddOpenAIModelClient(options => options.ApiKey = apiKey)
    .AddAgentForge()
    .AddAgentDefinition("openai", agent => agent
        .WithInstructions("Answer concisely.")
        .UseModel(model)
        .WithLimits(maxModelCalls: 4, maxToolCalls: 2))
    .BuildServiceProvider();

var agent = provider.GetRequiredKeyedService<IAgent>("openai");
var result = await agent.RunAsync("Reply with a short greeting.");
Console.WriteLine(result.FinalMessage.Text);
```

The adapter uses the OpenAI Responses API, disables SDK retries for AgentForge-owned clients, disables provider storage and parallel tool calls, and keeps provider SDK types out of provider-neutral contracts.

### Opt-in OpenAI live verification

The dedicated live-verification executable exercises buffered text, streaming text, a typed calculator-tool round trip, and caller cancellation through keyed `IAgent` instances. It is not a test project and cannot be discovered by `dotnet test`. All four gates must be present before the provider is constructed: `--live`, `AGENTFORGE_RUN_LIVE_TESTS=true`, a nonempty `OPENAI_API_KEY`, and a nonempty `OPENAI_MODEL`.

```powershell
$env:AGENTFORGE_RUN_LIVE_TESTS = 'true'
$env:OPENAI_API_KEY = '<your-key>'
$env:OPENAI_MODEL = '<your-model>'
dotnet run --project tests/AgentForge.LiveVerification/AgentForge.LiveVerification.csproj --configuration Release -- --live
```

Without every gate, the executable exits before provider construction or HTTP access and never prints environment-variable values. Four scenarios run sequentially. Text and cancellation scenarios allow one model call, the tool scenario allows two, every scenario permits at most one tool call, and each has a 30-second timeout. There are no retries. The current public provider adapter does not expose maximum output tokens, so that additional bound cannot be enforced. Live requests can incur API charges; choosing a small suitable `OPENAI_MODEL` reduces usage but is not a guaranteed cost ceiling.

Reports contain only scenario status, call counts, optional token usage, and fixed error classifications. `Pass` means observable checks held; `Fail` means a request or check failed; `Inconclusive` means the probabilistic model did not select the calculator tool; `Skipped` is reserved for a scenario that cannot run. Failed or inconclusive scenarios return nonzero, and no scenario is retried. Credentials, headers, raw requests/responses, tool arguments, provider exception messages, and inner exceptions are not printed or persisted.

The user reported a successful post-fix run against `gpt-4o-mini`: buffered passed with 1 model call and 0 tool calls; streaming passed with 1 model call and 0 tool calls; the typed-tool round trip passed with 2 model calls and 1 tool call; and caller cancellation passed with 0 completed model calls and 0 tool calls. This is user-reported evidence, not an independently rerun audit result. No run timestamp or exact commit was supplied. The run used source after the provider-compatible underscore tool-name correction, while the release audit identifies commit `f326b9030d1da5cd32e43d15910d72831577399d` plus uncommitted working-tree changes as its separate audited state.

Cancellation uses an already-cancelled caller token; it proves local propagation and absence of successful completion, not cancellation of remote work already accepted by OpenAI.

For Microsoft.Extensions.AI, install `AgentForge.Hosting` and `AgentForge.Adapters.MicrosoftExtensionsAI`, then pass a raw compatible `IChatClient` to `AddMicrosoftExtensionsAIModelClient` before `AddAgentForge`:

```csharp
using AgentForge.Abstractions;
using AgentForge.Adapters.MicrosoftExtensionsAI;
using AgentForge.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

// Obtain a raw IChatClient from your chosen Microsoft.Extensions.AI provider.
IChatClient chatClient = /* provider-specific construction */;
await using var provider = new ServiceCollection()
    .AddMicrosoftExtensionsAIModelClient(chatClient)
    .AddAgentForge()
    .AddAgentDefinition("interop", agent => agent
        .WithInstructions("Answer concisely.")
        .UseModel("client-selected-model")
        .WithLimits(maxModelCalls: 2, maxToolCalls: 1))
    .BuildServiceProvider();

var result = await provider.GetRequiredKeyedService<IAgent>("interop").RunAsync("Hello");
```

The `IChatClient` construction is intentionally provider-specific application code; AgentForge does not define a convenience factory for it. Clients wrapped in automatic tool-invocation middleware are unsupported because AgentForge must remain the sole tool authorization and execution boundary. The caller owns the `IChatClient` lifetime. See the complete credential-free implementation in `samples/AgentForge.Samples.MicrosoftExtensionsAI`.

Run the deterministic calculator sample with:

```powershell
dotnet run --project samples/AgentForge.Samples.Console --configuration Release
```

The credential-free streaming and interoperability samples are under `samples/AgentForge.Samples.Streaming` and `samples/AgentForge.Samples.MicrosoftExtensionsAI`. The OpenAI sample is under `samples/AgentForge.Samples.OpenAI` and exits without network access unless both environment variables are supplied.

## Supported scope and limitations

This preview supports in-process buffered and streaming runs, one explicitly registered model client per composition, keyed named agents, independent model/tool-call limits, deny-by-default sequential tools, typed tool validation, the OpenAI Responses adapter, and raw `Microsoft.Extensions.AI.IChatClient` interoperability.

It does not provide multi-agent orchestration, routing or fallback, retries, durable execution, persistence or memory, distributed execution, automatic MEAI tool middleware, parallel tools, process sandboxing, or framework-enforced token/output/payload-size limits. Callers must apply cancellation/timeouts and provider-side output limits where available. Streaming deltas are provisional; only `AgentRunCompleted` is authoritative. Tools are trusted in-process application code.

See [the architecture document](docs/architecture.md) for lifecycle invariants, package boundaries, security constraints, non-goals, and planned phases.
