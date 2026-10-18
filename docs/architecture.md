# AgentForge.NET Architecture

Status: Approved with amendments  
Last updated: 2026-10-18

## 1. Purpose

AgentForge.NET is an in-process, developer-first runtime for building reliable AI agents with idiomatic C# and .NET. It coordinates a language model, an explicitly permitted set of tools, and conversation state through a bounded execution loop. Provider SDKs, hosting, telemetry exporters, and future persistence implementations remain replaceable adapters around that loop.

The first milestone deliberately proves only the smallest useful architectural seam: one agent, one execution at a time, a deterministic fake language-model provider, deterministic tools, in-memory state, and well-defined termination and failure behavior.

## 2. Product principles

- AgentForge owns orchestration semantics; provider packages translate provider-specific protocols.
- Public contracts are small, strongly typed, async-first, cancellation-aware, and provider-independent.
- Composition and dependency injection are preferred over base classes and service location.
- Every execution is bounded. Tool availability is allow-listed per agent, and model-call and tool-call counts are independently limited.
- Failures are observable and preserve their cause. Cancellation is not converted into failure.
- Runtime state is explicit. Persistence, durability, and distribution are later capabilities, not hidden side effects.
- External dependencies are added only when a milestone needs them.
- Compatibility with `Microsoft.Extensions.AI` is implemented at adapter boundaries where useful; its types do not become AgentForge's foundational public contracts in the MVP.

## 3. MVP scope

### Included in milestone 1

- Immutable agent definition: identifier, instructions, model/provider selector, allowed tool names, and execution limits.
- A single-agent execution loop.
- Provider-neutral chat messages, model responses, tool calls, and usage metadata.
- A fake scripted LLM client for deterministic tests and samples.
- Explicitly registered, allow-listed tools with JSON input and output.
- Deterministic sequential tool execution in model-request order.
- In-memory conversation state scoped to one run.
- Terminal outcomes: completed, failed by exception, cancelled, or rejected because a limit/contract was violated.
- Maximum model-call and tool-call enforcement across an execution.
- Cancellation propagation through the runtime, provider, and tools.
- Structured logging hooks through `Microsoft.Extensions.Logging`.
- Dependency-injection registration and a minimal console sample.
- Unit tests for lifecycle, cancellation, provider/tool failure propagation, unknown/disallowed tools, malformed provider output, and tool-call limits.

### Explicit non-goals for milestone 1

- Multi-agent orchestration, handoffs, supervisors, or teams.
- Graph/DAG workflows and branching workflow definitions.
- Durable execution, resume, checkpointing, idempotency journals, or background workers.
- Streaming responses or streaming tool output.
- Retries, timeout policies, circuit breakers, or rate limiting beyond caller cancellation and hard execution limits.
- Persistent conversation memory, semantic/vector memory, embeddings, or retrieval.
- PostgreSQL, Redis, queues, leases, distributed locks, or distributed execution.
- A production OpenAI provider or any networked provider.
- OpenTelemetry activities, metrics, or exporters (logging seams and stable execution identifiers come first).
- ASP.NET Core endpoints or hosted services.
- Parallel tool execution, tool approval UI, process isolation, or sandboxing arbitrary code.
- Compatibility guarantees for pre-1.0 serialized data.

These are sequencing decisions, not rejections of the broader vision.

## 4. Solution structure

The proposed initial solution is smaller than the eventual package map:

```text
AgentForge.slnx
Directory.Build.props
global.json
src/
  AgentForge.Abstractions/
  AgentForge.Core/
  AgentForge.Hosting/
tests/
  AgentForge.Core.Tests/
samples/
  AgentForge.Samples.Console/
```

Projects for OpenAI, PostgreSQL, and dedicated observability are not scaffolded until they contain milestone-backed behavior. Empty packages imply stability and create dependency/versioning cost without value. `AgentForge.IntegrationTests` is also deferred because milestone 1 has no external integration boundary; deterministic runtime tests belong in `AgentForge.Core.Tests`.

Future projects may be added as follows:

- `AgentForge.Providers.OpenAI`: provider protocol translation; depends only on Abstractions and the vendor/MEAI client package.
- `AgentForge.Observability`: OpenTelemetry instrumentation and semantic conventions; depends on Abstractions, not Core internals.
- `AgentForge.Persistence.PostgreSQL`: durable state/checkpoint adapter after checkpoint semantics exist.
- `AgentForge.IntegrationTests`: tests real adapters against disposable infrastructure or provider contract fixtures.

### Dependency graph

```text
                         +-----------------------------+
                         | AgentForge.Samples.Console  |
                         +--------------+--------------+
                                        |
                         +--------------v--------------+
                         |     AgentForge.Hosting      |
                         +--------------+--------------+
                                        |
              +-------------------------+-------------------------+
              |                                                   |
  +-----------v-----------+                         +-------------v-------------+
  |    AgentForge.Core    |                         | AgentForge.Abstractions   |
  +-----------+-----------+                         +---------------------------+
              |                                                   ^
              +---------------------------------------------------+

  AgentForge.Core.Tests -> Core + Abstractions (+ Hosting only for DI tests)

  Future provider, persistence, and observability adapters -> Abstractions
  Core never references concrete adapters.
```

Rules:

- `Abstractions` has no dependency on other AgentForge projects and should use only BCL types unless a dependency is essential to a public contract.
- `Core` implements orchestration and depends on `Abstractions` plus logging abstractions.
- `Hosting` is the composition root extension package. It wires Core into `Microsoft.Extensions.DependencyInjection`; it contains no orchestration behavior.
- Samples reference Hosting and whichever adapters they demonstrate.
- No circular project references and no provider SDK types outside provider packages.

## 5. Core public contracts

Names and exact shapes may be refined during implementation, but their responsibilities should remain stable.

```csharp
public sealed record AgentDefinition(
    AgentId Id,
    string Instructions,
    ModelId Model,
    IReadOnlySet<string> AllowedTools,
    AgentExecutionLimits Limits);

public sealed record AgentExecutionLimits(int MaxModelCalls = 8, int MaxToolCalls = 8);

public sealed record AgentRunRequest(
    AgentDefinition Agent,
    IReadOnlyList<AgentMessage> Messages,
    string? ConversationId = null);

public interface IAgentRuntime
{
    Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);
}
```

`AgentRunResult` contains the execution ID, final assistant message, final transcript snapshot, and provider usage when known. Expected policy/contract violations use a typed `AgentExecutionException` with an error code. Unexpected provider and tool exceptions propagate with context and preserve the original exception as `InnerException`. `OperationCanceledException` propagates unchanged when the supplied token is cancelled.

The provider boundary represents one model turn:

```csharp
public interface IAgentModelClient
{
    Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ModelRequest(
    ModelId Model,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolDescriptor> AvailableTools);

public sealed record ModelResponse(
    AgentMessage AssistantMessage,
    IReadOnlyList<ToolCall> ToolCalls,
    ModelUsage? Usage = null);
```

The tool boundary separates metadata from invocation:

```csharp
public interface IAgentTool
{
    ToolDescriptor Descriptor { get; }

    Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}

public interface IAgentToolRegistry
{
    IReadOnlyList<ToolDescriptor> Describe(IReadOnlySet<string> allowedTools);
    bool TryResolve(string name, out IAgentTool tool);
}
```

Tool inputs and outputs use `JsonElement` at the orchestration boundary. This is intentionally less ergonomic than arbitrary generic methods but gives provider adapters a portable schema/payload shape without reflection in Core. A later tooling package can supply strongly typed delegates and JSON Schema generation without changing the runtime contract.

Messages are a discriminated content model rather than a single text property. An assistant message may contain text or a non-empty ordered collection of tool calls. Each tool call has a non-empty, batch-unique call ID, a tool name, and a valid JSON argument value. A tool-result message carries exactly one call ID matching a previously emitted assistant tool call and a JSON result. The runtime preserves assistant tool-call messages and correlated tool-result messages in the transcript, including ordering and identifiers, so future provider adapters can round-trip the conversation without reconstructing or losing information. Binary/multimodal content is deferred until a provider milestone establishes concrete requirements.

Strict model-response invariants are:

1. The response contains exactly one assistant message.
2. The assistant message contains either non-empty text or one or more tool calls, never both and never neither.
3. Tool-call IDs are non-empty and unique within the batch; tool names are non-empty; arguments contain valid JSON.
4. Tool calls returned separately from the assistant message are not permitted: the assistant message is the canonical representation stored in the transcript.
5. Usage counts, when supplied, are nonnegative.
6. Every tool-result message produced by the runtime has exactly one `ToolCallId` matching its originating assistant tool call.

## 6. Execution lifecycle

Each `RunAsync` invocation owns an isolated mutable execution context; registered services remain stateless and safe for concurrent callers.

```text
Validate request and limits
          |
          v
Create execution ID and transcript (system instructions + input messages)
          |
          v
Describe only the agent's allow-listed tools
          |
          v
Call model with transcript and tool descriptions <------------------+
          |                                                          |
          +-- final assistant response --> append --> complete       |
          |                                                          |
          +-- tool calls ---------------------------------------+    |
                                                              |    |
             validate response, allowance, and remaining limit |    |
                                                              v    |
             invoke each tool sequentially in declared order        |
                                                              |    |
             append correlated tool results to transcript -----------+
```

Lifecycle invariants:

1. Validate before calling external code: non-empty identifiers/instructions, positive model/tool limits, valid initial message roles, and known allowed tools.
2. Copy caller-owned collections into immutable/read-only snapshots so they cannot change during a run.
3. Check cancellation before every provider call and tool invocation, and pass the token to both.
4. Enforce the positive `MaxModelCalls` limit before every provider invocation.
5. Enforce the strict model-response invariants above.
6. Prevalidate the entire tool-call batch before invoking any tool: authorization, registration, non-empty and batch-unique call IDs, non-empty names, valid JSON arguments, and remaining tool-call budget. Any failure rejects the whole batch without side effects.
7. Resolve tools by exact ordinal name and verify each is allow-listed, even if the registry contains it.
8. Execute tools sequentially in model-provided order for deterministic behavior. Append one correlated result per successful call.
9. Stop immediately on the first provider/tool failure. Do not synthesize a successful model-visible result from an infrastructure exception.
10. A model final response ends the run. A response that keeps requesting tools eventually reaches a hard model-call or tool-call limit.

### Failure semantics

- Input and configuration errors: standard argument exceptions before execution starts.
- Runtime policy/contract errors: `AgentExecutionException` with stable `AgentExecutionErrorCode` values such as `ToolCallLimitExceeded`, `ToolNotAllowed`, `ToolNotFound`, and `InvalidModelResponse`.
- Provider/tool faults: `AgentProviderException` or `AgentToolException`, carrying execution/tool/call context and retaining the cause.
- Cancellation: `OperationCanceledException`; never wrapped as a provider or tool failure when the run token requested cancellation.

This initial exception-first API keeps the happy path small. A discriminated result union would make every call inspect status even though cancellation and dependency faults are exceptional in idiomatic .NET. Durable execution may later introduce persisted run status independently of this API.

## 7. Agent, provider, tool, and state interaction

- The agent definition is declarative policy: prompt, model choice, tool allow-list, and limits. It has no behavior and holds no service instances.
- The runtime is the policy-enforcing coordinator. It is the only component that advances the transcript or decides whether another model turn is allowed.
- The model client receives a snapshot of messages and only permitted tool descriptions. It cannot invoke tools itself.
- The model may request a tool by name and JSON arguments. The runtime validates the request and invokes the registered implementation.
- A tool receives immutable invocation context (execution ID, call ID, JSON arguments) and returns a JSON result. Tools do not mutate the transcript.
- The runtime appends correlated tool results and makes the next model call.
- Milestone 1 state exists only in the run-local transcript and is returned as a snapshot. No ambient session, static cache, or implicit persistence is used.

## 8. Security posture

Tool calling is capability-based: registration makes a tool available to the application, while an agent-specific allow-list grants use during a run. Both checks are required. Tool names use exact ordinal comparison, duplicate registrations fail at startup, arguments are treated as untrusted input, and raw secrets must not appear in descriptors, messages, exception text, or logs.

Milestone 1 does not claim sandboxed tool execution. Tools run in process with the application's identity, so application authors must register only trusted implementations. Approval workflows and out-of-process isolation require separate designs.

## 9. Observability approach

Core logs lifecycle events using structured fields such as execution ID, agent ID, model ID, tool name, call ID, turn number, duration, and outcome. Message content and tool arguments/results are excluded by default because they may contain sensitive data.

OpenTelemetry is deferred to a dedicated milestone so activity names, attributes, metrics, and redaction policy can be designed against working runtime semantics. Stable IDs and lifecycle boundaries in milestone 1 make later instrumentation additive.

## 10. Architectural tradeoffs

### Own minimal contracts vs expose Microsoft.Extensions.AI contracts

AgentForge needs durable orchestration semantics, error codes, tool policy, and future checkpoint serialization. Owning a small domain model prevents a third-party abstraction's release cadence or provider assumptions from becoming the framework's compatibility surface. MEAI interoperability should be delivered through adapters. The cost is mapping code; the benefit is control of versioning and semantics.

### In-memory transcript vs state-store abstraction now

No state-store interface is introduced in milestone 1. Until checkpoint boundaries, concurrency rules, consistency, and resume semantics are specified, such an interface would be speculative CRUD. The run-local transcript is enough to validate the loop. Persistence abstractions follow the durable-execution design.

### Sequential vs parallel tool calls

Sequential execution is deterministic, produces an unambiguous transcript, and avoids undeclared side-effect concurrency. Parallel execution can later be opt-in for tools that explicitly declare safety and ordering semantics.

### Exceptions vs result unions

Normal completion returns a value; invalid configuration, policy violations, dependency failures, and cancellation use idiomatic exceptions. Stable error codes retain machine-readability. Persisted workflow statuses are a distinct future concern.

### Separate Abstractions, Core, and Hosting from day one

The separation costs three small projects but creates real dependency-direction tests: adapter authors need only contracts, Core owns behavior, and DI remains optional. Conversely, empty future projects are not created.

## 11. Primary architectural risks and mitigations

| Risk | Why it matters | Initial mitigation |
|---|---|---|
| Public contracts ossify around one provider | Provider-specific concepts leak into every package | Provider-neutral messages, JSON tool payloads, contract tests, no SDK types in Abstractions |
| Unbounded model/tool loops | Cost, latency, and unintended side effects | Positive hard limits for both model and tool calls; enforce before invocation; reject invalid tool batches atomically |
| Unsafe tool execution | Model output is untrusted and tools have application privileges | Registration plus per-agent allow-list, validation, no dynamic code execution, conservative logging |
| Cancellation is swallowed or misclassified | Resource leaks and incorrect caller behavior | Pass one token end-to-end; preserve cancellation; dedicated tests at provider and tool boundaries |
| Mutable shared run state | Cross-request contamination and races | Stateless singleton-safe runtime dependencies; run-local state and snapshots |
| Error abstraction hides root causes | Production diagnosis becomes difficult | Typed contextual exceptions with stable codes and preserved inner exceptions |
| Premature persistence abstraction | Wrong transaction/checkpoint model becomes public API | Defer until durable lifecycle and idempotency semantics are designed |
| Telemetry leaks prompts or tool data | Sensitive information exposure | Metadata-only logging by default; explicit future redaction/content-capture policy |
| Framework churn (.NET 10 / MEAI versions) | Build instability and consumer friction | Pin SDK/dependencies centrally and isolate optional integrations |
| Fake provider gives false confidence | Deterministic tests do not prove wire compatibility | Clearly scope milestone; add provider contract and integration tests with first real adapter |

## 12. Phased implementation plan

### Phase 0: architecture and repository baseline

Deliverables:

- This architecture decision document and root `AGENTS.md`.
- Approved package boundaries, public contract direction, lifecycle, and failure policy.
- Minimal solution scaffold targeting .NET 10 with centralized build settings.

Acceptance criteria:

- Stakeholder approval of MVP/non-goals and tradeoffs.
- Project references conform to the dependency graph.
- `dotnet build` succeeds with zero warnings on an installed .NET 10 SDK.

### Phase 1: deterministic single-agent runtime

Deliverables:

- Contracts in Abstractions; execution loop in Core; DI registration in Hosting.
- Scripted fake provider and deterministic test tools in test/support code.
- Minimal console sample showing a final response and one tool round-trip.
- XML documentation on public APIs and a concise getting-started README.

Acceptance criteria:

- A no-tool provider response completes with the expected transcript and identifiers.
- One and multiple tool requests execute sequentially and correlated results reach the next model turn.
- Only allow-listed tools are described and invocable.
- Unknown/disallowed tool calls fail with the expected error code and execute no tool.
- An over-limit batch fails before any tool in that batch executes.
- Model-call and tool-call limits must be positive and are enforced before invocation.
- Every tool batch is prevalidated for authorization, registration, duplicate call IDs, JSON validity, and remaining budget before any tool executes.
- Caller cancellation is observed before a model call, during a model call, and during a tool call.
- Provider and tool exceptions preserve their cause and include execution context.
- Invalid/empty/ambiguous model responses fail deterministically.
- Concurrent runs do not share transcript or counters.
- `dotnet build` and `dotnet test` succeed with zero warnings.

### Phase 2: real provider and streaming

Design before implementation: streaming event model, partial failures, provider capability negotiation, MEAI interop, usage accounting, and secrets/configuration.

Acceptance criteria include provider contract tests, opt-in integration tests, cancellation during streaming, no secrets in logs, and equivalent non-streaming semantics.

### Phase 3: resilience and OpenTelemetry

Add explicit timeout/retry policy composition and OpenTelemetry activities/metrics. Define which operations are safe to retry and never retry side-effecting tools implicitly.

Acceptance criteria include deterministic retry tests, trace parent propagation, documented spans/metrics, and verified default redaction.

### Phase 4: durable state and PostgreSQL

First define execution events, checkpoint boundaries, optimistic concurrency, idempotency, schema versioning, and resume semantics. Then add persistence contracts and PostgreSQL adapter.

Acceptance criteria include crash/resume tests, concurrency-conflict tests, migrations, and disposable PostgreSQL integration tests.

### Phase 5: graphs and multi-agent orchestration

Build graph and multi-agent concepts on the durable lifecycle rather than embedding them in `IAgentRuntime`. Define scheduling, fan-out/fan-in, handoff context, budgets, and cycle limits.

Acceptance criteria include deterministic graph scheduling, bounded cycles, persisted resume, cancellation propagation, and isolated agent/tool capabilities.

### Phase 6: optional distributed and memory adapters

Add Redis coordination only for demonstrated distributed requirements. Add vector-memory adapters behind retrieval contracts derived from real use cases, with security and tenancy rules.

## 13. Environment prerequisite

.NET SDK 10.0.402 is installed and pinned in `global.json` with `latestPatch` roll-forward. Phase 1 build, tests, and sample execution have been verified using that SDK.

## 14. Approved decisions

1. Own minimal AgentForge public contracts and add MEAI adapters later.
2. Scaffold only Abstractions, Core, Hosting, Core.Tests, and Console sample.
3. Keep state run-local and defer a persistence interface.
4. Execute tool calls sequentially and reject over-limit batches atomically.
5. Use exceptions with stable error codes; preserve cancellation and inner causes.
6. Defer OpenTelemetry implementation while establishing metadata-safe logging seams.

The architecture was approved on 2026-10-18 with the model-call limit, atomic tool-batch validation, and strict message/correlation amendments incorporated above.

