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

## Quick start

Register exactly one provider-neutral model client, the runtime, and a named agent:

```csharp
var services = new ServiceCollection()
    .AddSingleton<IAgentModelClient>(modelClient)
    .AddAgentForge()
    .AddAgentDefinition("support", agent => agent
        .WithInstructions("Answer concisely.")
        .UseModel("configured-model")
        .WithLimits(maxModelCalls: 4, maxToolCalls: 2))
    .BuildServiceProvider();

var agent = services.GetRequiredKeyedService<IAgent>("support");
var result = await agent.RunAsync("How can I reset my password?", cancellationToken);
```

Each call creates a fresh run and preserves the configured model/tool limits. There is no unkeyed default agent. Duplicate agent keys, missing definitions, unknown allowed tools, and multiple model clients fail deterministically before a model invocation.

## Typed tools and authorization

```csharp
services.AddAgentTool(AgentTool.Create<AddArguments, AddResult>(
    "calculator.add",
    "Adds two numbers.",
    static (args, context, token) =>
        ValueTask.FromResult(new AddResult(args.Left + args.Right))));

services.AddAgentDefinition("calculator", agent => agent
    .WithInstructions("Use the calculator for arithmetic.")
    .UseModel("configured-model")
    .AllowTool("calculator.add")
    .WithLimits(3, 2));
```

Registration makes a tool available to the application; only `AllowTool` authorizes it for that agent. Arguments remain untrusted and typed validation occurs atomically before any tool in a requested batch executes.

## Streaming

```csharp
await foreach (var update in agent.StreamAsync("Hello", cancellationToken))
{
    if (update is AssistantTextDelta delta)
        Console.Write(delta.Text);
    else if (update is AgentRunCompleted completed)
        Console.WriteLine($"Model calls: {completed.Result.ModelCalls}");
}
```

A non-streaming provider fails with `CapabilityNotSupported`; AgentForge never silently switches modes. Cancellation is propagated to the runtime, provider, and tools.

## Providers

For OpenAI, set `OPENAI_API_KEY` and the deployment-specific `OPENAI_MODEL`; never commit credentials. Register `AddOpenAIModelClient(...)` before `AddAgentForge()`. The opt-in OpenAI sample does not run during default verification and no live provider call is claimed.

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

Live verification has been implemented but **has not been executed**. Offline tests validate safeguards and deterministic behavior, not live API compatibility. Cancellation uses an already-cancelled caller token; it proves local propagation and absence of successful completion, not cancellation of remote work already accepted by a provider.

For Microsoft.Extensions.AI, pass a raw compatible `IChatClient` to `AddMicrosoftExtensionsAIModelClient`. Clients wrapped in automatic tool-invocation middleware are unsupported because AgentForge must remain the sole tool authorization and execution boundary.

Run the deterministic calculator sample with:

```powershell
dotnet run --project samples/AgentForge.Samples.Console --configuration Release
```

The credential-free streaming and interoperability samples are under `samples/AgentForge.Samples.Streaming` and `samples/AgentForge.Samples.MicrosoftExtensionsAI`. The OpenAI sample is under `samples/AgentForge.Samples.OpenAI` and exits without network access unless both environment variables are supplied.

See [the architecture document](docs/architecture.md) for lifecycle invariants, package boundaries, security constraints, non-goals, and planned phases.
