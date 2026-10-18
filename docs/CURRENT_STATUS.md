# AgentForge.NET Current Status

Status date: 2026-10-18  
Current milestone: Phase 1 complete  
Target framework: .NET 10 (`net10.0`)  
Verification SDK: 10.0.402

This document describes the implementation currently present in this repository. Items under **Verified** were exercised locally using the commands and environment recorded below. Design goals and future work are not presented as implemented behavior.

## Verified Phase 1 implementation

### Runtime and execution lifecycle

- A single-agent, in-process execution loop is implemented by `AgentRuntime`.
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
  - `InvalidModelResponse`.
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
dotnet test AgentForge.slnx --configuration Release
```

Result:

```text
Passed!  - Failed: 0, Passed: 42, Skipped: 0, Total: 42
Target: .NETCoreApp,Version=v10.0
```

The 42 tests cover terminal completion, immutable public snapshots, nested JSON ownership, transcript composition, usage aggregation and overflow, tool-call/result correlation, deterministic tool ordering, positive and cumulative limits, atomic invalid-batch rejection, execution-wide call-ID uniqueness, cancellation before/during/after asynchronous boundaries, provider/tool failure propagation, malformed/disposed provider and tool JSON, null tool results, registration and descriptor snapshots, descriptor filtering, logging redaction, tool-enabled concurrency isolation, and DI composition.

Console sample:

```powershell
dotnet run --project samples\AgentForge.Samples.Console\AgentForge.Samples.Console.csproj --configuration Release
```

Result:

```text
20 + 22 = 42
Model calls: 2; Tool calls: 1
```

These results verify the checked-in Phase 1 implementation on the listed SDK and local Windows environment. They do not verify behavior on other operating systems, other SDK patches, external model providers, or distributed infrastructure.

## Unimplemented features

The following are intentionally not implemented and have not been verified:

- Real OpenAI or other networked LLM providers.
- Microsoft.Extensions.AI adapters.
- Streaming model responses or streaming tool output.
- Multi-agent orchestration, handoffs, supervisors, or teams.
- Graph/DAG workflows, branches, fan-out, or fan-in.
- Durable execution, checkpoints, resume, idempotency journals, or background workers.
- Persistent conversation state or memory.
- PostgreSQL, Redis, vector stores, embeddings, or retrieval.
- Retries, timeouts, circuit breakers, rate limiting, or provider fallback.
- OpenTelemetry activities, metrics, semantic conventions, or exporters.
- ASP.NET Core endpoints or hosted-service integration.
- Parallel tool execution.
- Tool schema validation, tool approval workflows, process isolation, or sandboxing.
- Multimodal message content.
- NuGet packaging, API compatibility baselines, signing, CI, or release automation.

The broader product vision and later phases in `docs/architecture.md` remain plans, not verified capabilities.

## Known limitations

- Initial `AgentRunRequest.Messages` currently accepts one or more non-empty `UserMessage` instances only. Supplying prior assistant/tool history is rejected even though the internal message model can represent it.
- Tool argument validation guarantees an owned, defined `JsonElement`; Core does not validate arguments against `ToolDescriptor.ParametersSchema`.
- Tool descriptors and implementations are trusted application configuration. Tool code can access all permissions available to the host process.
- Tool calls execute sequentially. There is no opt-in declaration for read-only or concurrency-safe tools.
- Failures stop execution immediately. No recovery state or partial transcript is returned to the caller.
- The scripted model client is a deterministic development/test component and does not demonstrate provider protocol compatibility.
- Metadata-only structured logging and redaction are regression tested, but there is no dedicated telemetry package or configurable content-capture policy.
- The repository-local `.dotnet-home` directory is an ignored CLI artifact and is not product source.

## Outstanding issues

- There is no CI workflow to reproduce the local .NET 10 build and test evidence.
- Public API compatibility is not yet tracked with an API baseline tool.
- Provider capability and finish-reason semantics are not defined.
- Streaming event types, cancellation after partial output, and partial-failure semantics are not designed.
- Usage normalization across providers is not defined.
- Content logging/redaction remains deny-by-default but has no configurable policy model.
- Integration tests do not exist because Phase 1 contains no external adapter.

None of these outstanding issues invalidate the verified Phase 1 acceptance results, but they must not be described as implemented capabilities.

## Next recommended task

Prepare a Phase 2 architecture proposal for review without implementing it. The proposal should define:

1. the streaming event model and terminal/partial-failure semantics;
2. provider capability negotiation and finish reasons;
3. a Microsoft.Extensions.AI adapter boundary;
4. provider contract-test fixtures reusable by real adapters;
5. usage normalization and cancellation behavior;
6. credential handling, redaction, and opt-in integration-test policy.

Implementation of a real provider or any other Phase 2 feature should begin only after that proposal is explicitly approved.
