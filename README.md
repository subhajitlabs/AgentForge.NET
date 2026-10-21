# AgentForge.NET

AgentForge.NET is an in-process, provider-neutral AI agent runtime for .NET 10. It provides bounded buffered and streaming execution, explicit tool authorization, typed tools, OpenAI and Microsoft.Extensions.AI adapters, and named agents through keyed dependency injection.

## Package responsibilities

- `AgentForge.NET`: dependency-only convenience metapackage for the recommended Hosting, Tools, and OpenAI package set.
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

The current coordinated preview is `0.2.0-preview.2`. The recommended quick-start installation is:

```powershell
dotnet add package AgentForge.NET --version 0.2.0-preview.2
```

`AgentForge.NET` is a dependency-only convenience metapackage, not a separate runtime. It directly installs `AgentForge.Hosting`, `AgentForge.Tools`, and `AgentForge.Providers.OpenAI`; Core and Abstractions resolve transitively. All modular packages remain independently installable. For example:

```powershell
dotnet add package AgentForge.Hosting --version 0.2.0-preview.2
```

The Microsoft.Extensions.AI adapter remains optional and is deliberately not included in the metapackage:

```powershell
dotnet add package AgentForge.Adapters.MicrosoftExtensionsAI --version 0.2.0-preview.2
```

Applications that need contracts without the runtime can continue to reference `AgentForge.Abstractions` directly.

## Complete offline quick start

Create a .NET 10 console application, install `AgentForge.NET` (or `AgentForge.Hosting` for a minimal modular installation), and replace `Program.cs` with this complete credential-free example:

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

An optional whole-execution timeout can be configured per named agent. It is disabled by default for compatibility:

```csharp
.AddAgentDefinition("support", agent => agent
    .WithInstructions("Answer concisely.")
    .UseModel("offline-script")
    .WithLimits(maxModelCalls: 1, maxToolCalls: 1)
    .WithTimeout(TimeSpan.FromSeconds(30)))
```

The timeout reaches buffered and streaming model work, stream enumeration, tools, and runtime completion boundaries. Caller cancellation continues to throw `OperationCanceledException`; a runtime deadline throws `AgentExecutionException` with `ExecutionTimedOut`, and caller cancellation wins if both are active. Cancellation is cooperative: a provider or in-process tool that ignores its token may continue running, but its late result is rejected and cannot complete the execution. AgentForge does not automatically retry providers or tools.

Optional resource budgets are also configured per agent and are disabled by default:

```csharp
.AddAgentDefinition("support", agent => agent
    .WithInstructions("Answer concisely.")
    .UseModel("offline-script")
    .WithLimits(maxModelCalls: 2, maxToolCalls: 2)
    .WithTimeout(TimeSpan.FromSeconds(30))
    .WithResourceLimits(
        maxInputCharacters: 32_000,
        maxOutputCharacters: 8_000,
        maxToolArgumentBytes: 16_384,
        maxToolResultBytes: 65_536))
```

Observed token budgets are a separate, optional execution-wide policy:

```csharp
services.AddAgentDefinition("support", agent => agent
    .WithInstructions("Help the user.")
    .UseModel("provider-model")
    .WithTokenBudget(
        maxInputTokens: 20_000,
        maxOutputTokens: 4_000,
        maxTotalTokens: 24_000));
```

These budgets use only provider-reported usage and accumulate across model turns. They do not estimate tokens from text. Equality is accepted; exceeding any configured input, output, or total limit fails with `TokenBudgetExceeded`. Because usage normally arrives only when a turn ends, the policy is retrospective: one provider call can exceed a budget before AgentForge can stop later model or tool work. A configured budget fails closed with `TokenUsageUnavailableOrInvalid` when reliable usage is missing or malformed.

Text budgets use .NET `string.Length` (UTF-16 code units). The input budget is recalculated from system, user, and assistant text in the complete transcript before every model call. The output budget is cumulative for the execution; streamed text is checked before each delta is published, and terminal buffered text is checked before it is accepted. Tool argument and result budgets are cumulative per execution and use the UTF-8 byte count of each JSON value's raw representation. Equality is accepted. Exceeding a budget never truncates content: it throws `AgentExecutionException` with `InputSizeLimitExceeded`, `OutputSizeLimitExceeded`, `ToolArgumentSizeLimitExceeded`, or `ToolResultSizeLimitExceeded`. An oversized argument batch executes no tools; an oversized result is rejected after the tool has run and does not reverse its side effects.

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

## Distributed tracing

Core emits optional BCL `ActivitySource` instrumentation from source `AgentForge`. The source version is the Core assembly informational version. No OpenTelemetry SDK, exporter, or telemetry configuration is required; when no `ActivityListener` subscribes, `StartActivity` returns `null` and execution behavior is unchanged.

The activity hierarchy follows work that actually starts:

```text
agentforge.agent.execute
  agentforge.model.invoke
  agentforge.tool.invoke
  agentforge.model.invoke
```

Subscribe with an `ActivityListener` whose `ShouldListenTo` accepts `source.Name == "AgentForge"`, or configure an OpenTelemetry provider to include that source. Ambient `Activity.Current` is the execution parent; model and tool activities are children of the execution activity. Buffered calls use `agentforge.operation.mode=buffered`, while asynchronous stream enumeration uses `streaming` and remains open through provider enumeration, event delivery, terminal completion, failure, cancellation, or early disposal.

The closed tag schema is `agentforge.operation.type`, `agentforge.operation.mode`, `agentforge.outcome`, `agentforge.error.code`, `agentforge.model.turn`, `agentforge.tool.invocation`, `agentforge.finish.reason`, `agentforge.usage.input_tokens`, `agentforge.usage.output_tokens`, `agentforge.provider.error.kind`, and `agentforge.provider.is_transient`. Success leaves the standard activity status unset; failures and policy rejections use `ActivityStatusCode.Error`. Caller cancellation is `cancelled`/`caller_cancelled` without error status, while a runtime deadline is `timeout`/`executiontimedout` with error status.

Activities never record prompts, instructions, responses, tool names, tool-call IDs, arguments, results, agent/model/tenant identifiers, credentials, headers, provider bodies, request IDs, raw exception messages, or exception objects. Trace/span IDs provide correlation. Provider SDKs may independently create child activities, and consumers remain responsible for exporter configuration, sampling, retention, and any telemetry added by their own providers or tools.

## Metrics and terminal diagnostics

Core also owns a BCL `Meter` named `AgentForge`, versioned with the Core assembly informational version. It requires no OpenTelemetry SDK or exporter. The instruments are:

| Instrument | Kind | Unit | Recording point |
|---|---|---|---|
| `agentforge.agent.executions` | Counter | `{execution}` | Once when an execution starts |
| `agentforge.agent.execution.duration` | Histogram | `s` | Once at terminal completion, covering the buffered run or streaming enumeration |
| `agentforge.model.invocations` | Counter | `{invocation}` | Immediately before each actual provider invocation |
| `agentforge.model.invocation.duration` | Histogram | `s` | Once when that provider invocation terminates |
| `agentforge.tool.invocations` | Counter | `{invocation}` | Immediately before each actual tool invocation |
| `agentforge.tool.invocation.duration` | Histogram | `s` | Once when that tool invocation terminates |
| `agentforge.agent.failures` | Counter | `{failure}` | Once per terminal failed execution; caller cancellation and early disposal are not failures |
| `agentforge.model.tokens` | Counter | `{token}` | Once per authoritative terminal usage report, tagged `input` or `output` |

Metric attributes use only `agentforge.operation.type`, `agentforge.operation.mode`, `agentforge.outcome`, `agentforge.error.code`, `agentforge.provider.error.kind`, and `agentforge.token.type`. Missing usage emits no token measurement; zero is recorded only when a provider authoritatively reports zero. Streaming deltas never generate token measurements.

One `AgentExecutionDiagnostic` is written for every started execution to the `AgentForge` `DiagnosticListener` under event name `AgentForge.Execution.Terminal`. Subscribe through `DiagnosticListener.AllListeners`, select the listener named `AgentForge`, and subscribe to that event. The payload contains only execution mode, terminal outcome, stable error code and provider-neutral failure kind when applicable, actual model/tool invocation counts, nullable observed input/output tokens, and elapsed duration. Outcomes distinguish success, provider/tool failure, caller cancellation, runtime timeout, policy/resource/provider-processing/token-budget rejection, streaming failure, and `stream_disposed` early disposal.

Metric and diagnostic dimensions never include prompts, responses, arguments, results, exception messages, credentials, or agent/model/tool/tenant/execution/call/provider-request/trace identifiers. There is no diagnostic queue: records are synchronously offered only to enabled listeners. With no listener, the diagnostic payload is not allocated. Consumers own aggregation, export, retention, and callback safety.

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

### Failure classification and retry ownership

Provider adapters report sanitized `ModelProviderException` values. Authentication, authorization, invalid-request, model-not-found, protocol, and unknown failures are permanent by default. Rate limiting, service unavailability, timeouts, and transport failures are marked potentially transient. `IsTransient` means only that a later attempt might succeed; it does not prove the failed request was unaccepted or that replay is safe. `RetryAfter` is accepted only when nonnegative and no greater than 24 hours, and provider request IDs accept only a conservative ASCII identifier subset.

AgentForge Core performs exactly one provider invocation per model turn and never retries a tool. Streaming deltas remain provisional, and a failure before or after a delta cannot produce `AgentRunCompleted`. YFlow.NET or another durable workflow owner may decide whether to start a new execution using application-specific idempotency and recovery state. Caller-supplied OpenAI clients and Microsoft.Extensions.AI pipelines own any retries configured below AgentForge; avoid layering those retries unintentionally. AgentForge-owned OpenAI clients continue to set the SDK retry count to zero.

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
        .WithLimits(maxModelCalls: 2, maxToolCalls: 1)
        .WithResourceLimits(
            maxInputCharacters: 32_000,
            maxOutputCharacters: 8_000,
            maxToolArgumentBytes: 16_000,
            maxToolResultBytes: 32_000,
            maxProviderStreamEvents: 2_000,
            maxProviderContentCharacters: 8_000,
            maxProviderToolArgumentBytes: 16_000))
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

This preview supports in-process buffered and streaming runs, one explicitly registered model client per composition, keyed named agents, independent model/tool-call limits, optional Core input/output/tool-payload limits, optional adapter processing limits, deny-by-default sequential tools, typed tool validation, the OpenAI Responses adapter, and raw `Microsoft.Extensions.AI.IChatClient` interoperability.

Core character and byte resource limits govern the execution transcript, accepted output, and tool payloads across a complete run. Adapter processing limits independently bound SDK stream-event processing and per-turn adapter-owned text and function-argument accumulation. Observed token budgets instead govern cumulative provider-reported usage after completed turns. Provider-native maximum-output-token controls act remotely before generation and remain distinct from both local resource limits and retrospective observed budgets. Applications should combine these layers where available. Content is rejected rather than truncated, streaming deltas remain provisional, and only `AgentRunCompleted` is authoritative.

This preview does not provide multi-agent orchestration, routing or fallback, retries, durable execution, persistence or memory, distributed execution, automatic MEAI tool middleware, parallel tools, process sandboxing, token estimation, or provider-native output-token configuration. Tools are trusted in-process application code.

See [the architecture document](docs/architecture.md) for lifecycle invariants, package boundaries, security constraints, non-goals, and planned phases.
