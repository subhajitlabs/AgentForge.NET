# AgentForge.NET Current Status

Status date: 2026-10-18  
Current milestone: Phase 2 Increment 8 (Opt-in Live Verification) implemented; live execution not performed
Target framework: .NET 10 (`net10.0`)  
Verification SDK: 10.0.402

This document describes the implementation currently present in this repository. Items under **Verified** were exercised locally using the commands and environment recorded below. Design goals and future work are not presented as implemented behavior.

## Implemented Phase 2 Increment 8

- `tests/AgentForge.LiveVerification` is a dedicated executable rather than a test project. It reaches the official OpenAI adapter only through keyed public `IAgent` facades and the existing buffered/streaming runtime and typed-tool engine.
- Provider construction requires all four gates: `--live`, `AGENTFORGE_RUN_LIVE_TESTS=true`, nonempty `OPENAI_API_KEY`, and nonempty `OPENAI_MODEL`. A closed gate exits before construction or HTTP access without printing values.
- Four scenarios run sequentially: buffered text, streaming text with exactly one terminal event, a typed `calculator.add` round trip, and deterministic pre-cancelled caller-token propagation.
- Text/cancellation scenarios allow one model call, the tool scenario allows two, every scenario allows at most one tool call, and each has a 30-second timeout. There are no automatic retries. The current public provider configuration cannot set maximum output tokens; this is documented rather than claimed as enforced.
- Output is limited to safe names/statuses, call counts, optional usage, and fixed classifications. Tool non-selection is inconclusive and is not retried; fail or inconclusive returns nonzero.
- `tests/AgentForge.LiveVerification.Tests` provides offline guard coverage for the gates, early exit, whitespace configuration, fixed limits, redacted reporting, and exclusion of the executable from test discovery.
- Live verification has **not been executed**. Offline build/test evidence does not prove live OpenAI compatibility.

## Verified Phase 2 Increment 7 implementation

- `IAgent` exposes the approved immutable `Definition`, buffered `RunAsync(string)`, and streaming `StreamAsync(string)` surface.
- `AddAgentDefinition(name, configure)` builds the existing immutable `AgentDefinition` and registers a keyed singleton facade. There is no unkeyed fallback or `IAgentFactory`.
- The facade creates one fresh `AgentRunRequest` and `UserMessage` per invocation and delegates unchanged to `IAgentRuntime` or `IStreamingAgentRuntime`.
- Duplicate names fail during registration. Missing keys do not fall back. Unknown allowed tools and model-client cardinality other than exactly one fail on first resolution before provider invocation.
- Tool registration remains distinct from per-agent authorization; limits, atomic validation, sequential execution, cancellation, results, and events remain owned by the existing runtime.
- The calculator sample demonstrates named resolution and a typed explicitly allowed tool while retaining its established output. Separate deterministic streaming and Microsoft.Extensions.AI samples compile and run offline. The OpenAI sample is opt-in through `OPENAI_API_KEY` and `OPENAI_MODEL` and is not run by default.
- No live provider verification, routing, fallback, retry, memory, persistence, graph, multi-agent, or Increment 8 behavior was added.

## Verified Phase 2 Increment 6 implementation

- `AgentForge.Adapters.MicrosoftExtensionsAI` wraps a caller-owned `Microsoft.Extensions.AI.IChatClient` as both `IAgentModelClient` and `IStreamingAgentModelClient`. Core and Abstractions have no Microsoft.Extensions.AI dependency.
- The adapter pins stable `Microsoft.Extensions.AI.Abstractions` 10.11.0. This is the current abstractions-only package for libraries implementing or consuming `IChatClient`; its .NET 10 asset has no additional package dependencies.
- Implementation was verified against the 10.11.0 public `IChatClient.GetResponseAsync` and `GetStreamingResponseAsync` signatures; `ChatMessage`, `ChatRole`, `TextContent`, `FunctionCallContent`, `FunctionResultContent`, `ChatOptions`, declaration-only `AIFunctionFactory.CreateDeclaration`, `ChatResponse`, `ChatResponseUpdate`, `ChatFinishReason`, `UsageDetails`, `UsageContent`, and `ChatResponseExtensions.ToChatResponse` surfaces.
- Every turn converts the complete transcript in order. System/user/assistant text, assistant function calls, tool-role function results, call IDs, names, JSON arguments/results, model ID, and only `ModelRequest.AvailableTools` are preserved without provider-managed conversation state.
- Tool definitions are non-invocable `AIFunctionDeclaration` instances. The adapter never registers AgentForge delegates with the external client and does not authorize or execute tools.
- Buffered responses require exactly one assistant message and map `Stop`, `ToolCalls`, `Length`, and `ContentFilter` explicitly. Missing or extensible unknown finish reasons map to `ModelFinishReason.Unknown`; contradictory shapes, unsupported content, malformed calls, duplicate IDs, and invalid usage fail as sanitized protocol errors.
- Streaming forwards only non-empty text fragments and privately accumulates updates. Exactly one finish-bearing update is required; usage-only updates may follow it. The adapter consumes through clean EOF, reconstructs with the package's standard `ToChatResponse` contract, validates parity, and emits one authoritative completion. Missing/duplicate/conflicting terminal information and post-terminal model content fail explicitly.
- Caller cancellation is propagated before calls, during buffered awaits and streaming enumeration, and before completion. `NotSupportedException` maps to `CapabilityNotSupported`; other safe HTTP/timeout/transport evidence maps to the existing provider-neutral taxonomy with fixed messages and no retries.
- Minimal singleton DI registration accepts an existing client or factory, refuses to replace an existing AgentForge model-client registration, and leaves `IChatClient` lifetime/disposal ownership with the caller.
- No Increment 7 facade, provider routing, automatic tool execution, retry, persistence, live verification, or Microsoft.Extensions.AI dependency in foundational packages was added.

## Verified Phase 2 Increment 5 implementation

- `AgentForge.Providers.OpenAI` adapts the official OpenAI .NET SDK 2.14.0 Responses API to `IAgentModelClient` and `IStreamingAgentModelClient` without adding provider dependencies to Core.
- Provider-owned construction validates and snapshots the API key and endpoint, requires HTTPS except for explicitly enabled loopback HTTP, rejects URI user-info/fragments, and disables SDK retries with `ClientRetryPolicy(0)`.
- Every request is stateless and sends the full ordered AgentForge transcript with `store=false`, `parallel_tool_calls=false`, no previous response/conversation/background state, and only already-authorized function-tool definitions. Hosted tools and SDK-side tool execution are not enabled.
- System/user/assistant text, assistant function calls, correlated function outputs, schemas, names, call IDs, and owned JSON arguments/results map through the SDK's public Responses types. Function definitions use `strict=false` to preserve AgentForge serializer/schema semantics.
- Buffered responses map completed text, completed function calls, refusals, incomplete reasons, cancellation, provider failure, usage, and malformed/contradictory protocol states to the approved provider-neutral contracts.
- Streaming forwards only validated text deltas. Function-argument fragments remain private and are checked against done events and the authoritative terminal response. Completion is emitted only after exactly one valid terminal event and clean SDK enumeration; EOF-before-terminal, duplicate/post-terminal events, malformed data, and contradictions fail explicitly.
- `ModelProviderException` and `ModelProviderErrorKind` provide sanitized provider-neutral classification, transient/retry metadata, and conservatively validated provider request IDs. AgentForge-owned messages never copy provider bodies/messages; the SDK exception remains the potentially sensitive inner cause.
- `OpenAIProviderOptions`, `OpenAIAgentModelClient`, and minimal DI registration are the only provider public surfaces. Caller-owned `ResponsesClient` construction is supported with an explicit ownership boundary for endpoint and retry policy.
- The Responses API remains marked experimental by SDK 2.14.0. `OPENAI001` is suppressed only in the provider project, with an upgrade-review comment; tests acknowledge it locally per source file.
- No live OpenAI call, beginner facade, provider-managed conversation, retry/fallback, hosted tool, or later-increment capability was added.

## Verified Phase 2 Increment 4 implementation

- `AgentForge.Tools` provides `AgentTool.Create<TArguments, TResult>` and immutable `ToolExecutionContext` for strongly typed delegate tools without Core or Hosting dependencies.
- Registration clones and freezes `JsonSerializerOptions`, resolves one argument and result `JsonTypeInfo`, exports the argument schema through the .NET 10 JSON schema API, closes object schemas to unknown properties, and retains the same frozen metadata for validation and invocation.
- The supported argument subset includes sealed POCOs and records, strings, booleans, integral/floating-point numbers and decimals, nullable values, string enums, arrays, `List<T>`, and nested supported objects.
- Registration rejects non-object roots, `object`, delegates, streams, open generics, dictionaries, polymorphism, reference cycles, extension data, unsupported custom converters/resolvers, case-insensitive property matching, contracts deeper than 16 levels, and contracts exceeding 128 properties.
- `IAgentToolArgumentValidator` is an optional provider-neutral capability. Core invokes it during the existing atomic batch-prevalidation phase after structural, uniqueness, authorization, registration, and budget checks and before any tool executes.
- Invalid typed arguments fail with `InvalidToolArguments` and a fixed redacted message. Existing untyped tools remain compatible.
- Typed invocation deserializes and serializes with the frozen metadata, propagates cancellation, preserves execution/tool-call identifiers, and returns owned JSON through the existing `ToolResult` contract.
- Hosting adds only the non-generic `AddAgentTool(IAgentTool)` instance overload. Registration does not authorize a tool; agent allow-lists remain mandatory.
- No Microsoft.Extensions.AI adapter, beginner facade, retry, persistence, parallel execution, or other later-increment capability was added.

## Verified Phase 1 implementation

All Phase 1 guarantees described below remain verified after the Increment 4 typed-tool addition.

## Verified Phase 2 Increment 3 implementation

- `IStreamingAgentModelClient` adds an optional provider capability that streams immutable `ModelTextDelta` events followed by one authoritative `ModelResponseCompleted`.
- `IStreamingAgentRuntime` streams immutable `AssistantTextDelta` events and emits exactly one authoritative `AgentRunCompleted` on success.
- `AgentRuntime` implements both runtime interfaces while preserving the existing `IAgentRuntime.RunAsync` contract and behavior.
- Streaming and non-streaming calls use the same internal `AgentExecutionEngine` orchestration loop and therefore share request validation, allow-list enforcement, model/tool budgets, transcript ownership, finish-reason policy, usage accounting, atomic tool-batch prevalidation, sequential tool execution, cancellation checks, and error translation.
- Core defensively rejects missing or repeated terminal completion, null or unknown events, events after completion, invalid deltas, malformed terminal responses, and text deltas associated with a tool-call response.
- Core consumes each provider stream through natural termination before accepting its terminal response. Tool execution cannot begin until the complete stream ends cleanly and the entire tool batch passes existing prevalidation.
- Streamed text is provisional. Provider, policy, tool, or cancellation failure after text emits no `AgentRunCompleted`.
- Runtime event delivery uses cancellation-linked, acknowledged backpressure. Early consumer disposal cancels local execution before it can advance to later provider events or tool execution. This does not guarantee cancellation of remote work already accepted by a provider.
- `CapabilityNotSupported` identifies streaming requests made against a model client that does not implement `IStreamingAgentModelClient`; failure occurs before provider invocation.
- `ScriptedStreamingAgentModelClient` provides deterministic offline streaming turns for tests.
- `AddAgentForge()` exposes the same `AgentRuntime` singleton through both `IAgentRuntime` and `IStreamingAgentRuntime`.
- No real provider, typed-tool package, Microsoft.Extensions.AI integration, facade, retry, persistence, parallel execution, or other later-increment capability was added.

## Verified Phase 2 Increment 2 implementation

- `AgentRuntime` remains the unchanged public `IAgentRuntime.RunAsync` entry point and delegates execution to an internal `AgentExecutionEngine`.
- The internal engine owns the single orchestration loop and centralizes request and allow-list validation, run-local state, model/tool counters, transcript construction, response and finish-reason validation, usage accounting, atomic tool-batch prevalidation, sequential tool execution, cancellation checks, and provider/tool error translation.
- Each execution owns an isolated internal context containing its execution ID, immutable input-derived tool policy and descriptors, transcript, counters, aggregate usage, and execution-wide tool-call ID set.
- Model invocation is supplied to the engine as an internal asynchronous turn delegate. A future streaming path can consume provider events inside such a delegate and return the authoritative terminal `ModelResponse` to the same loop; it will therefore reuse the engine's limits, authorization, validation, accounting, transcript, tool execution, cancellation, and error policies.
- No streaming contracts or implementation, public APIs, packages, provider adapters, typed tools, retries, persistence, parallel execution, or other later-increment capabilities were added.

## Verified Phase 2 Increment 1 implementation

- `ModelFinishReason` is a provider-neutral enum with `Completed`, `ToolCalls`, `LengthLimit`, `ContentFiltered`, `Refused`, `Cancelled`, and `Unknown` values.
- `ModelResponse` requires an explicit finish reason through its public constructor.
- The public constructor validates that the assistant-message shape is compatible with the finish reason:
  - `Completed` requires non-empty text and no tool calls;
  - `ToolCalls` requires one or more tool calls and no text;
  - incomplete, filtered, and refused outcomes may contain optional partial text but no tool calls;
  - `Cancelled` requires no useful assistant content;
  - `Unknown` may contain mapped text but no executable tool calls.
- `ModelResponse.Completed(...)` and `ModelResponse.ToolCalls(...)` factories make successful response construction explicit.
- `AgentRuntime` accepts only `Completed` as successful terminal output and only `ToolCalls` as an executable tool turn.
- Length-limited, filtered, and refused output fails with stable execution error codes.
- `Unknown` is classified as `InvalidModelResponse`.
- A provider-reported `Cancelled` reason propagates caller cancellation when the caller token is cancelled; without caller cancellation it is classified as an `AgentProviderException` with its cause preserved.
- No streaming contract, provider adapter, typed-tool package, facade, or other later Phase 2 increment is implemented.

### Runtime and execution lifecycle

- A single-agent, in-process execution loop is implemented by the internal `AgentExecutionEngine`; `AgentRuntime` is the public facade over it.
- Each run receives a generated `ExecutionId` and owns an isolated transcript and counters.
- The runtime prepends the agent's system instructions to the supplied user messages.
- The runtime repeatedly invokes one `IAgentModelClient` until it receives a terminal assistant text response or fails.
- `MaxModelCalls` and `MaxToolCalls` must both be positive.
- The model-call limit is enforced before each provider invocation.
- The tool-call limit is enforced against the entire requested batch before any tool invocation.
- Model and tool operations receive the caller's `CancellationToken`.
- Cancellation is checked before and after awaited provider/tool operations and before successful completion. Cancellation requested through that token propagates as `OperationCanceledException` rather than being wrapped as a provider or tool failure.
- Successful runs return the terminal assistant message, a transcript snapshot, aggregate reported usage, and model/tool call counts.
- Concurrent runs maintain independent execution IDs, transcripts, and counters.

### Tool execution and safety boundary

- Tools implement `IAgentTool` and are registered in an immutable `AgentToolRegistry`; descriptor metadata and schema JSON are snapshotted at registration.
- Duplicate registered tool names and empty registered names are rejected when the registry is created.
- Tool names use ordinal comparison.
- Registration does not grant permission: an agent must also list the tool name in `AllowedTools`.
- Unknown allow-list entries are rejected before the model is invoked.
- Only registered tools included in the agent allow-list are described to the model.
- Every model-requested tool batch is prevalidated before any tool in the batch runs.
- Batch validation checks:
  - remaining tool-call budget;
  - non-empty call IDs and tool names;
  - call-ID uniqueness across the entire execution;
  - defined JSON arguments;
  - agent authorization;
  - tool registration.
- Any invalid call rejects the entire batch without partial tool execution.
- Valid calls execute sequentially in model-provided order.
- Batch validation is atomic, but tool invocation is not transactional: if a later tool fails, successful earlier tool side effects are not rolled back.
- Each result is appended as a `ToolResultMessage` carrying the originating call ID and tool name.
- Tools run in process with the application's identity. No sandbox or approval workflow is implemented.

### Model and message contract

- `AgentMessage` is a provider-neutral discriminated hierarchy containing:
  - `SystemMessage`;
  - `UserMessage`;
  - `AssistantMessage`;
  - `ToolResultMessage`.
- An assistant response must contain exactly one of:
  - non-empty terminal text; or
  - one or more ordered tool calls.
- Empty and ambiguous assistant responses are rejected.
- Negative provider usage values are rejected.
- Assistant tool-call messages remain in the transcript; tool results preserve explicit correlation rather than replacing or reconstructing the assistant message.
- Tool arguments, schemas, and results use `JsonElement` at the provider-neutral boundary.
- Public JSON-bearing contracts clone values and do not depend on the originating `JsonDocument` lifetime.
- Ordered public collections are non-mutable snapshots, and agent tool allow-lists are frozen snapshots.
- `ModelUsage` aggregates nonnegative provider-reported input and output token counts across calls using checked arithmetic.

### Failure behavior

- Invalid caller configuration fails before model invocation with argument exceptions.
- Runtime policy and model-contract violations use `AgentExecutionException` and a stable `AgentExecutionErrorCode`.
- Implemented error codes are:
  - `ModelCallLimitExceeded`;
  - `ToolCallLimitExceeded`;
  - `ToolNotAllowed`;
  - `ToolNotFound`;
  - `InvalidModelResponse`;
  - `ModelOutputIncomplete`;
  - `ModelOutputFiltered`;
  - `ModelRefused`.
- Provider failures are wrapped in `AgentProviderException` with the execution ID and original exception preserved as `InnerException`.
- Tool failures are wrapped in `AgentToolException` with execution ID, tool name, call ID, and original exception preserved.
- Null, undefined, or disposed JSON tool results are reported as `AgentToolException` after invocation.
- Model-controlled tool names, call IDs, arguments, and results are excluded from exception messages; tool name and call ID remain available as structured exception properties.

### Hosting, fake provider, and sample

- `AddAgentForge()` registers `IAgentRuntime` and `IAgentToolRegistry` with Microsoft dependency injection.
- `AddAgentTool<TTool>()` registers trusted application tool implementations.
- `ScriptedAgentModelClient` provides a deterministic FIFO sequence of fixed responses or asynchronous response functions.
- The scripted client is safe for concurrent dequeue operations but each scripted step is consumed exactly once.
- The console sample registers a calculator tool and a two-step scripted model interaction through dependency injection.
- The sample requests `20 + 22`, executes one tool call, and produces the result `42`.

## Public API decisions

- AgentForge owns small provider-neutral contracts instead of exposing provider SDK or Microsoft.Extensions.AI types from foundational packages.
- `AgentDefinition` is declarative data containing identity, instructions, model selection, an explicit tool allow-list, and hard limits.
- IDs are distinct value types: `AgentId`, `ModelId`, and `ExecutionId`.
- Public asynchronous operations use the `Async` suffix and accept a trailing optional `CancellationToken`.
- Public messages and requests use immutable records with owned JSON values and non-mutable collection snapshots.
- `AgentRunRequest` contains only the agent definition and initial messages; the unused `ConversationId` placeholder was removed during Phase 1 hardening.
- Normal completion returns `AgentRunResult`; invalid configuration, policy violations, dependency failures, and cancellation use exceptions.
- Provider adapters implement one-turn `IAgentModelClient.CompleteAsync` semantics. The model client does not execute tools.
- Every `ModelResponse` explicitly states its `ModelFinishReason`; the runtime never infers successful completion solely from message shape.
- Tool implementations expose metadata separately from invocation through `ToolDescriptor` and `InvokeAsync`.
- `AgentForge.Abstractions` contains domain contracts, `AgentForge.Core` owns execution policy, and `AgentForge.Hosting` owns DI composition.
- Package versions and build rules are centralized in `Directory.Packages.props` and `Directory.Build.props`.

## Verification evidence

The following results were freshly verified on 2026-10-18. The CLI home was redirected to the ignored repository-local `.dotnet-home` directory, first-time experience was disabled, and telemetry was opted out for the command process.

SDK check:

```powershell
dotnet --version
```

Result:

```text
10.0.402
```

Release build:

```powershell
dotnet build AgentForge.slnx --configuration Release
```

Result:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Test run:

```powershell
dotnet test AgentForge.slnx --configuration Release --no-build
```

Result:

```text
Passed!  - Failed: 0, Passed: 201, Skipped: 0, Total: 201
Target: .NETCoreApp,Version=v10.0
```

The 201 tests include 104 Core/runtime/Hosting tests, 30 typed-tool tests, 32 OpenAI provider tests, 23 Microsoft.Extensions.AI adapter tests, and 12 live-verification guard tests. Hosting coverage includes buffered and streaming facade delegation, fresh execution state, cancellation, keyed isolation, duplicate/missing keys, unknown tools, multiple-client rejection, and unsupported streaming. Adapter and guard coverage remains deterministic, credential-free, and network-free.

Console sample:

```powershell
dotnet run --project samples\AgentForge.Samples.Console\AgentForge.Samples.Console.csproj --configuration Release --no-build
```

Result:

```text
20 + 22 = 42
Model calls: 2; Tool calls: 1
```

These results verify the offline implementation through Phase 2 Increment 8 on the listed SDK and local Windows environment. Provider, adapter, and live-guard tests are deterministic and credential-free; they do not constitute execution of live verification or prove behavior on other operating systems, SDK versions, external providers, or distributed infrastructure.

## Unimplemented features

The following are intentionally not implemented and have not been verified:

- Other networked LLM providers and live OpenAI verification.
- Streaming tool output.
- Multi-agent orchestration, handoffs, supervisors, or teams.
- Graph/DAG workflows, branches, fan-out, or fan-in.
- Durable execution, checkpoints, resume, idempotency journals, or background workers.
- Persistent conversation state or memory.
- PostgreSQL, Redis, vector stores, embeddings, or retrieval.
- Retries, timeouts, circuit breakers, rate limiting, or provider fallback.
- OpenTelemetry activities, metrics, semantic conventions, or exporters.
- ASP.NET Core endpoints or hosted-service integration.
- Parallel tool execution.
- Tool approval workflows, process isolation, or sandboxing.
- Multimodal message content.
- NuGet packaging, API compatibility baselines, signing, CI, or release automation.

The broader product vision and later phases in `docs/architecture.md` remain plans, not verified capabilities.

## Known limitations

- Initial `AgentRunRequest.Messages` currently accepts one or more non-empty `UserMessage` instances only. Supplying prior assistant/tool history is rejected even though the internal message model can represent it.
- Untyped tools receive only the existing structural JSON checks unless they implement `IAgentToolArgumentValidator`.
- Typed tools intentionally support a bounded schema subset; case-insensitive property matching, dictionaries, arbitrary converters/resolvers, polymorphism, and general external JSON Schema validation are not supported.
- Tool descriptors and implementations are trusted application configuration. Tool code can access all permissions available to the host process.
- Tool calls execute sequentially. There is no opt-in declaration for read-only or concurrency-safe tools.
- Failures stop execution immediately. No recovery state or partial transcript is returned to the caller.
- Streaming text deltas are provisional and are not returned as recovery state after failure.
- Stream cancellation is cooperative. Local disposal prevents later Core work, but cannot guarantee cancellation of remote work already accepted by a provider.
- The scripted model client is a deterministic development/test component and does not demonstrate provider protocol compatibility.
- The OpenAI Responses SDK surface is experimental in pinned version 2.14.0 and must be contract-reviewed before upgrade.
- Caller-owned `ResponsesClient` instances cannot be audited by AgentForge for endpoint security or disabled retry policy.
- Typed streaming does not expose a convenient raw response header surface, so streaming provider request IDs remain unavailable.
- The abstractions-only Microsoft.Extensions.AI surface cannot reliably identify a middleware pipeline that invokes tools automatically. Such wrapped clients are unsupported; supply a raw client that returns tool calls to AgentForge.
- Microsoft.Extensions.AI has no standard refusal finish reason in 10.11.0. Extensible unrecognized finish reasons map conservatively to `Unknown` rather than being guessed as refusals.
- The Microsoft.Extensions.AI adapter owns neither lifetime nor disposal of its caller-supplied `IChatClient`.
- Metadata-only structured logging and redaction are regression tested, but there is no dedicated telemetry package or configurable content-capture policy.
- The repository-local `.dotnet-home` directory is an ignored CLI artifact and is not product source.

## Outstanding issues

- There is no CI workflow to reproduce the local .NET 10 build and test evidence.
- Public API compatibility is not yet tracked with an API baseline tool.
- Provider capability negotiation beyond the implemented `IStreamingAgentModelClient` interface check is not implemented.
- Usage normalization across providers is not defined.
- Content logging/redaction remains deny-by-default but has no configurable policy model.
- Live verification is separately opt-in and has not been executed; its deterministic guard coverage is offline only.

None of these outstanding issues invalidate the verified Phase 1 acceptance results, but they must not be described as implemented capabilities.

## Next recommended task

Stop after Increment 8. Perform the final Phase 2 architecture and release-readiness review. Do not begin Phase 3 without explicit approval.
