# AgentForge.NET Phase 3 Architecture Proposal

Status: Approved — Increments 1 through 8 implemented locally; Phase 3 complete pending coordinated prerelease commit
Date: 2026-10-20
Scope: Production resilience, resource safety, diagnostics, and OpenTelemetry-compatible instrumentation

## Implementation progress

- Increment 1, stable outcomes and timeout semantics: implemented locally on 2026-10-20.
- The implementation adds the approved optional whole-execution timeout and `ExecutionTimedOut` classification without adding retries, resource-size limits, token budgets, or telemetry.
- Increment 2, provider failure and retry-boundary hardening: implemented locally on 2026-10-20.
- The implementation keeps Core single-attempt, makes transient classification explicitly advisory, bounds and sanitizes provider metadata, verifies terminal streaming/tool boundaries, and documents durable retry ownership without adding a retry API.
- Increment 3, input, output, and tool-payload bounds: implemented locally on 2026-10-21.
- The implementation adds optional per-agent Core limits for full transcript text before provider calls, cumulative accepted/streamed output text, and cumulative UTF-8 JSON argument/result bytes. Argument batches remain atomic, streaming crossing deltas are not emitted, and no payload is truncated or copied into limit errors.
- Increment 4, provider processing and streaming bounds: implemented locally on 2026-10-21.
- The implementation adds optional per-turn SDK streaming-event, adapter-retained text, and provider tool-argument limits, propagates the immutable policy through model requests, translates adapter overflow to a stable non-transient Core failure, and replaces the runtime event channel with explicit capacity one. SDK-internal pre-delivery buffering remains outside AgentForge's control.
- Increment 5, observed token-budget policy: implemented locally on 2026-10-21.
- The implementation adds disabled-by-default execution-wide input, output, and total budgets over provider-reported terminal usage. Missing required usage fails closed, invalid usage has a stable non-transient classification, and streaming snapshots are counted once at the authoritative turn boundary.
- Increment 6, Activity instrumentation: implemented locally on 2026-10-21.
- Core now emits execution, model-turn, and tool-invocation activities from the versioned `AgentForge` source. Buffered and streaming lifecycles share stable content-free outcome tags, ambient parent propagation, and deterministic cancellation/failure closure without an exporter dependency. Metrics and structured terminal diagnostics were deferred at that increment.
- Increment 7, metrics and structured terminal diagnostics: implemented locally on 2026-10-21.
- Core now emits versioned, low-cardinality BCL metrics for execution/model/tool counts and durations, terminal failures, and authoritative reported token usage. One bounded, content-free `AgentForge.Execution.Terminal` diagnostic closes each execution, including early streaming disposal, without an exporter, logging provider, or queue.
- Increment 8, documentation, compatibility, regression hardening, and release readiness: implemented locally on 2026-10-21.
- The implementation adds focused cross-feature regressions and completes locked restore, zero-warning Release build, offline tests, package-graph/metadata validation, isolated local-package consumer validation, documentation review, formatting, and diff hygiene. It adds no runtime feature, dependency, retry, or version change.

## 1. Current architecture assessment

### Repository baseline

- Branch: `main`.
- HEAD at inspection: `ae2c78b5db3d609d876bff1afd7776f001145950`.
- Published preview tag: `v0.2.0-preview.1` at `92ebd9c`.
- Working tree: clean before and after inspection.
- SDK: .NET `10.0.402`.
- Package version: `0.2.0-preview.1`.
- Solution: six production packages, four samples, five automated test projects, and one gated live-verification executable.
- No nested `AGENTS.md` files were found.

The Phase 3 design inspection ran:

```powershell
dotnet build AgentForge.slnx --configuration Release
dotnet test AgentForge.slnx --configuration Release --no-build
```

The build passed with zero warnings and zero errors. All 203 deterministic tests passed: 104 Core, 31 Tools, 33 OpenAI, 23 Microsoft.Extensions.AI, and 12 live-verification guard tests. No live OpenAI request was made.

### Package and dependency boundaries

The implementation conforms to the intended dependency direction:

- `AgentForge.Abstractions` contains provider-neutral public contracts and immutable values.
- `AgentForge.Core` contains the execution engine, runtime, registry, and deterministic scripted clients.
- `AgentForge.Hosting` contains dependency injection, keyed agent definitions, and the `IAgent` facade.
- `AgentForge.Tools` contains typed-tool construction and bounded schema validation.
- `AgentForge.Providers.OpenAI` contains the OpenAI Responses adapter.
- `AgentForge.Adapters.MicrosoftExtensionsAI` contains raw `IChatClient` interoperability.

Provider SDK types do not leak into `AgentForge.Abstractions`. Core does not reference either concrete provider.

### Execution architecture

`AgentExecutionEngine` is the single policy loop for buffered and streaming runs. Current behavior includes:

- An isolated execution context for every run.
- Immutable request, descriptor, transcript, JSON, and allow-list snapshots.
- Positive model-call and tool-call limits.
- Limit checks before invocation.
- Whole-batch tool prevalidation before any tool side effect.
- Sequential tool execution.
- Exact ordinal tool authorization.
- Execution-wide unique tool-call IDs.
- Aggregated provider usage with checked arithmetic.
- Explicit finish-reason enforcement.
- No automatic retry.
- Provider and tool failures wrapped with execution context while preserving inner exceptions.
- Caller cancellation propagated through model calls, streaming enumeration, tools, and iterator disposal.

Buffered and streaming execution share authorization, budgets, transcript mutation, usage accounting, and failure rules. Phase 3 must preserve that single policy path.

### Streaming architecture

`AgentRuntime` adapts provider streaming through the same engine:

- Text deltas are provisional.
- `AgentRunCompleted` is the only authoritative success event.
- The runtime rejects missing, duplicate, contradictory, or post-terminal events.
- Early consumer disposal cancels and awaits the producer.
- An acknowledgement per emitted event prevents the producer from running materially ahead of the consumer.

The internal channel is nevertheless created as unbounded. Acknowledgement currently keeps practical occupancy close to one item, but that invariant is implicit and fragile. A bounded capacity-one channel should express and enforce it directly.

Provider stream accumulators construct complete text and function arguments in memory without a size ceiling.

### Cancellation and timeout behavior

Cancellation handling is already comparatively strong:

- Pre-cancelled tokens prevent provider invocation.
- Cancellation is checked before and after provider and tool awaits.
- Provider and tool cancellation caused by the caller's token remains `OperationCanceledException`.
- Cancellation is tested at buffered, streaming, provider, tool, and disposal boundaries.
- A provider returning after ignoring cancellation is still rejected as cancellation.

There is no runtime-owned deadline or timeout. Applications must create and manage a `CancellationTokenSource`, and AgentForge cannot distinguish an application deadline from arbitrary caller cancellation.

Provider-originated timeouts are classified as `ModelProviderErrorKind.Timeout`, but that is distinct from an AgentForge execution deadline. Because cancellation is cooperative, AgentForge cannot guarantee prompt termination when an in-process provider or tool ignores the supplied token. This limitation must remain explicit.

### Retry behavior

There is no AgentForge runtime retry loop:

- The AgentForge-owned OpenAI client explicitly configures SDK retry count to zero.
- A caller-supplied `ResponsesClient` may have caller-configured retries.
- A caller-supplied Microsoft.Extensions.AI pipeline may contain retry middleware.
- Provider exceptions expose `Kind`, `IsTransient`, `RetryAfter`, and an optional sanitized provider request ID.
- Tool failures have no retry classification and are never retried.

This is aligned with YFlow.NET owning durable retry and recovery. The remaining concern is documenting and enforcing retry ownership clearly enough to avoid layered retries.

### Logging and diagnostics

Core currently logs only:

- Execution start with execution, agent, and model identifiers.
- Successful completion with execution ID and model/tool-call counts.

Prompts, responses, arguments, results, and credentials are not logged by AgentForge product code.

Current gaps are:

- No structured terminal event for failure, cancellation, timeout, or limit rejection.
- No duration measurement.
- No model- or tool-boundary logs.
- No stable unified outcome classification.
- Full logging of preserved inner exceptions by an application can expose provider- or tool-owned sensitive text.
- Agent and model identifiers may themselves contain tenant-sensitive or high-cardinality values.

### Provider failure classification

`ModelProviderException` already distinguishes authentication, authorization, invalid request, model not found, rate limiting, timeout, service unavailable, transport, protocol, and unknown failures.

OpenAI and Microsoft.Extensions.AI adapters classify many status and transport failures and preserve `IsTransient`. Unknown failures default to non-transient, which is the safe retry posture.

However, transient means only that another attempt might work. It does not prove that the previous request had no effect. Transport failures, timeouts, and some service failures can be ambiguous after request acceptance. This is especially important for streaming after a delta has been observed.

### Resource limits

Implemented limits and protections are:

- Model-call limit.
- Tool-call limit.
- Checked aggregate token accounting when usage is reported.
- Typed-tool schema depth and property-count limits.
- OpenAI function-name validation.
- Sequential tools and disabled OpenAI parallel tool calls.

Not implemented are:

- Input text or transcript-size limits.
- Tool argument/result payload-size limits.
- Buffered output-size limits.
- Streaming cumulative output limits.
- Provider event-count or per-delta limits.
- Runtime token budgets.
- Provider maximum-output-token configuration.
- Bounded response parsing or accumulation.
- An explicit execution deadline.

Token usage is reported only after a provider turn. It can support accounting and stopping later turns, but it cannot retroactively impose a hard ceiling on the turn that already exceeded the budget.

### OpenTelemetry status

No `ActivitySource`, `Meter`, OpenTelemetry package, exporter, or telemetry project exists. Exporter-neutral instrumentation can use `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter` from the BCL, so no mandatory OpenTelemetry SDK or exporter dependency is needed.

### External compatibility surface

YFlow.NET is not present in this repository, so its source-level usage could not be independently inspected. Repository documentation identifies the public `AgentForge.Hosting` package and keyed `IAgent` resolution as the integration seam.

The compatibility-critical surface is:

- `services.AddAgentForge()`.
- `services.AddAgentDefinition(name, configure)`.
- Existing `AgentDefinitionBuilder` methods.
- Keyed `IAgent` registration and resolution.
- `IAgent.RunAsync(string, CancellationToken)`.
- `IAgent.StreamAsync(string, CancellationToken)`.
- Existing exception types and error codes.
- Current model/tool-call behavior.
- One model client per composition.

These signatures and keyed registration behavior must remain unchanged.

## 2. Phase 3 gaps and risks

| Area | Gap | Risk |
|---|---|---|
| Deadline | No explicit execution timeout | Runs can consume resources indefinitely when callers omit a deadline |
| Cancellation classification | All caller-token cancellation looks alike | Operations cannot reliably distinguish an AgentForge timeout from caller cancellation |
| Cooperative cancellation | Providers and tools may ignore tokens | A timeout cannot forcibly terminate in-process work |
| Retry ownership | Classification exists but ownership is distributed | Runtime, SDK, middleware, and YFlow could accidentally layer retries |
| Ambiguous failures | Transient does not express whether work may have been accepted | Unsafe automatic replays, especially after streaming begins |
| Input size | Transcript and message content are unbounded | Memory, latency, and provider-cost exposure |
| Output size | Buffered and streamed output is unbounded | Memory exhaustion and uncontrolled output |
| Tool payloads | Arguments and results have no byte or character ceiling | Large provider data or trusted-tool results can expand the transcript |
| Streaming | Provider accumulators use unbounded builders | A long stream can consume memory before terminal validation |
| Token budgets | Usage is only accumulated, not governed | No cost-oriented stopping policy across turns |
| Diagnostics | Only start and success logs exist | Failures, cancellations, timeouts, and bottlenecks are hard to diagnose |
| Metrics | No counters or histograms | No production health or latency visibility |
| Traces | No runtime, model, or tool spans | No boundary timing or ambient correlation |
| Sensitive metadata | IDs and inner exceptions may contain application or provider data | Accidental disclosure through logs or telemetry |
| Cardinality | Agent, model, and tool identifiers are unconstrained strings | Metric-series explosion if used as metric tags |
| Compatibility | Extending positional records can break binary or source consumers | YFlow preview integration could fail even for additive-looking changes |

## 3. Proposed technical design

### A. Resilience

#### Execution timeout semantics

Add a runtime-owned, optional maximum execution duration with the following semantics:

1. The caller's token remains authoritative.
2. AgentForge creates a linked execution token only when a timeout is configured.
3. If the caller token is cancelled, propagate `OperationCanceledException`.
4. If the AgentForge deadline expires while the caller token is not cancelled, throw `AgentExecutionException` with a new stable `ExecutionTimedOut` error code and a safe message.
5. If caller cancellation and timeout race, check the caller token first so explicit caller cancellation wins.
6. Apply the same rule to buffered execution, streaming enumeration, model calls, tools, event delivery, and disposal.
7. Do not return a partial `AgentRunResult` on timeout.
8. Already emitted streaming deltas remain provisional and are not converted into completion.
9. Document that timeout cancellation is cooperative and cannot stop a non-cooperative in-process dependency.

The initial increment should support one whole-execution timeout rather than independent model and tool timeouts. Per-boundary deadlines introduce more policy interactions and ambiguous tool outcomes and should be added only when evidence justifies them.

#### Retry ownership

AgentForge Core should perform no automatic retries in Phase 3.

Rationale:

- YFlow.NET owns durable retry and recovery.
- The runtime cannot know whether an arbitrary provider request was accepted before a transport failure.
- Retrying a streamed request after emitting output would produce duplicate or contradictory events.
- Tool calls may be side-effecting and have no idempotency contract.
- Provider SDK or Microsoft.Extensions.AI middleware may already retry below AgentForge.

Phase 3 should instead:

- Retain and tighten provider failure classification.
- Document `IsTransient` as advisory, never proof that replay is safe.
- Treat protocol, authentication, authorization, invalid request, model-not-found, and unknown failures as non-retryable by default.
- Treat rate limiting, service unavailability, timeout, and transport failures as potentially transient but possibly ambiguous.
- Preserve sanitized `RetryAfter` values.
- Record internally whether a streaming failure occurred before or after the first emitted delta for diagnostics, without adding automatic replay.
- Never retry tools.
- Document that externally supplied provider clients own and must disclose their retry behavior.

If founder approval requires an AgentForge retry policy, it should be a later opt-in provider-only feature, disabled by default, bounded by attempt count and execution deadline, and permitted only before any streamed output or tool execution. It should not be part of the foundational increment.

### B. Resource safety

Introduce a separate immutable resource policy rather than expanding the existing positional `AgentExecutionLimits` constructor. The conceptual limits are:

- `MaxExecutionDuration`.
- `MaxInputCharacters`.
- `MaxOutputCharacters`.
- `MaxToolArgumentBytes`.
- `MaxToolResultBytes`.
- `MaxProviderStreamEvents`.
- Optional `MaxTotalTokens`.

Keep `MaxModelCalls` and `MaxToolCalls` in the existing type for compatibility.

#### Enforcement points

- Validate configured limits as positive before execution.
- Count system instructions plus all initial and accumulated transcript text before every provider call.
- Measure JSON payloads by UTF-8 byte count, not `JsonElement.GetRawText().Length`.
- Validate a complete tool-call batch, including every argument's size, before executing any tool.
- Validate each tool result before adding it to the transcript.
- Count cumulative streamed text before publishing each delta.
- Count provider stream events before accepting the next event.
- Validate terminal buffered text before appending or returning it.
- Enforce limits before the next provider or tool invocation wherever possible.
- Use checked arithmetic and classify overflow as invalid provider output or a resource-limit failure.
- Reject limit excess consistently with stable error codes such as:
  - `InputSizeLimitExceeded`.
  - `OutputSizeLimitExceeded`.
  - `ToolArgumentSizeLimitExceeded`.
  - `ToolResultSizeLimitExceeded`.
  - `ProviderStreamEventLimitExceeded`.
  - `TokenBudgetExceeded`.
  - `ExecutionTimedOut`.

For streaming, a delta that would cross the output limit must not be emitted. The stream terminates with the stable limit exception and never emits `AgentRunCompleted`.

#### Token policy

`MaxTotalTokens` must be described as an observed-usage budget:

- If a provider reports usage, aggregate it with checked arithmetic.
- If accumulated reported usage reaches or exceeds the configured budget, prevent another model invocation.
- If one invocation reports usage above the remaining budget, fail after that invocation.
- Do not claim that this prevents the provider from generating or billing tokens for that invocation.
- If usage is absent, the token budget cannot be enforced.

When a token budget is configured but usage is unavailable, the design must choose either fail closed or treat the budget as advisory and expose unknown usage. Fail closed is recommended because it is predictable, but it requires founder approval.

Provider-native maximum-output-token options may later reduce cost and generation size, but they cannot replace runtime response-size validation and should remain capability-specific.

#### Bounded provider processing

Core must enforce provider-neutral bounds. Adapters should also enforce them as early as their SDK permits:

- OpenAI and Microsoft.Extensions.AI streaming accumulators receive configured per-turn bounds.
- String and function-argument builders check size before append.
- Event counts are checked before processing.
- Buffered responses are checked immediately after SDK return and before copying into runtime state.
- The runtime's unbounded channel is replaced with a bounded capacity-one channel while retaining acknowledgement and early-disposal behavior.

An SDK may fully buffer a response before AgentForge can inspect and reject it. AgentForge must not claim transport-level hard bounds unless the SDK exposes them.

### C. OpenTelemetry

Instrumentation should live in Core because Core owns lifecycle boundaries. A separate observability package is unnecessary unless later functionality requires exporter-specific dependencies.

Use only:

- `System.Diagnostics.ActivitySource`.
- `System.Diagnostics.Metrics.Meter`.

No OpenTelemetry exporter dependency should be introduced.

#### Activities

Use one source, versioned with the assembly:

- Source name: `AgentForge`.

Define these spans:

- `agentforge.agent.execute`: one span per run, parented to ambient `Activity.Current`, covering validation through terminal success or failure.
- `agentforge.model.invoke`: one child span per actual provider invocation, covering the buffered call or complete provider stream enumeration.
- `agentforge.tool.invoke`: one child span per actual tool invocation.

Default span tags should include only stable metadata:

- Operation kind.
- Buffered versus streaming mode.
- Outcome.
- Stable AgentForge error code.
- Provider error kind.
- Retryable advisory flag.
- Model or tool call ordinal.
- Finish reason.
- Token counts when reported.

Do not record by default:

- Prompts or responses.
- Tool arguments or results.
- API keys, endpoints, connection strings, or headers.
- Provider exception messages.
- Tool exception messages.
- Tool-call IDs.
- Provider request IDs.
- Execution IDs as tags.
- Arbitrary tenant, agent, model, or tool names.

Trace and span IDs provide correlation. Existing `ExecutionId` may remain structured log metadata, but it must not be a metric dimension.

Record exceptions only through sanitized classification tags by default. Do not attach the full exception object because preserved inner exceptions may contain sensitive provider or application content.

#### Metrics

Use one meter:

- Meter name: `AgentForge`.

Recommended instruments are:

- `agentforge.agent.executions`: counter.
- `agentforge.agent.execution.duration`: histogram in seconds.
- `agentforge.model.invocations`: counter.
- `agentforge.model.invocation.duration`: histogram in seconds.
- `agentforge.tool.invocations`: counter.
- `agentforge.tool.invocation.duration`: histogram in seconds.
- `agentforge.model.tokens`: counter tagged only by input or output.
- `agentforge.resource.limit_exceeded`: counter.

Metric tags must be a closed, low-cardinality set:

- `operation.mode`: buffered or streaming.
- `outcome`: success, error, cancelled, timeout, or rejected.
- `error.type`: stable bounded classification.
- `provider.error.kind`: enum value.
- `finish.reason`: enum value.
- `token.type`: input or output.

Execution, agent, model, tool, provider-request, tenant, and call IDs must not be metric tags.

### D. Diagnostics

Define a shared internal outcome classifier used by logs, activities, and metrics:

- Success.
- Caller-cancelled.
- Execution-timeout.
- Policy-rejected.
- Provider-failed.
- Tool-failed.
- Invalid-provider-response.

Logging should emit:

- Run started at `Information`.
- Run completed at `Information`.
- Caller cancellation at `Information`.
- Policy or resource rejection at `Warning`.
- Provider or tool failure at `Warning` or `Error` according to a documented policy.

Structured properties should be stable and content-free:

- `ExecutionId`.
- `OperationMode`.
- `Outcome`.
- `ErrorCode`.
- `ProviderErrorKind`.
- `IsTransient`.
- `ModelCalls`.
- `ToolCalls`.
- `InputTokens`.
- `OutputTokens`.
- `DurationMs`.

Avoid logging agent, model, and tool identifiers by default unless explicitly classified as safe application metadata. Existing start logging includes agent and model IDs; Phase 3 should either remove them or put them behind explicit opt-in. That behavioral change requires approval.

Trace propagation requires no custom carrier in this in-process runtime: starting activities under `Activity.Current` preserves ambient parentage. Provider SDKs may create their own child activities independently.

### E. Compatibility

Preserve all existing signatures and behavior by default:

- Do not change `IAgent`, `IAgentRuntime`, or `IStreamingAgentRuntime`.
- Do not change keyed registration.
- Do not add members to existing positional record constructors.
- Do not require consumers to reference an OpenTelemetry package.
- Keep the existing `AddAgentForge()` overload.
- Keep the current `AgentRuntime` constructor.
- Keep one configured model client per composition.
- Keep tool execution sequential.
- Keep retries disabled by default.

Additive options can be introduced through:

- A new `AgentRuntimeOptions` or `AgentResourceLimits` immutable options type.
- A new `AddAgentForge(Action<AgentRuntimeOptions>)` overload.
- A new `AgentRuntime` constructor overload while preserving the existing constructor.
- Additive `AgentExecutionErrorCode` values.
- Optional builder methods such as `WithTimeout` or `WithResourceLimits` only if per-agent policy is approved.

Default values require care. Unlimited defaults maximize behavioral compatibility but conflict with the repository rule that execution paths be bounded. The recommended resolution is to choose conservative finite defaults for newly registered agents while allowing existing low-level constructor behavior to remain compatible for one preview cycle, with a documented migration. This decision requires founder approval.

## 4. Ordered implementation increments

Foundational semantics precede instrumentation. Each increment must remain independently reviewable and verifiable.

### Increment 1 — Stable outcomes and timeout semantics

#### Objective

Establish the failure vocabulary and one end-to-end execution deadline before adding retries or telemetry.

#### Projects and files likely to change

- `docs/architecture.md`.
- `src/AgentForge.Abstractions/Exceptions.cs`.
- A new options or resource-policy file in Abstractions or Core.
- `src/AgentForge.Core/AgentExecutionEngine.cs`.
- `src/AgentForge.Core/AgentRuntime.cs`.
- `src/AgentForge.Hosting/ServiceCollectionExtensions.cs`.
- `src/AgentForge.Hosting/AgentDefinitionBuilder.cs`.
- Core and Hosting tests.
- README.

#### Public API changes

- Add stable timeout and resource error codes.
- Add options or configuration overloads without removing or changing existing members.
- Preserve all current entry points.

#### Failure and cancellation semantics

- Caller cancellation remains `OperationCanceledException`.
- Runtime deadline becomes `AgentExecutionException` with `ExecutionTimedOut`.
- Caller cancellation wins a race with timeout.
- No automatic retry.
- No completion event after timeout.

#### Test requirements

- Pre-start timeout.
- Timeout during buffered provider call.
- Timeout during stream enumeration.
- Timeout during tool execution.
- Caller cancellation versus timeout race.
- Non-cooperative dependency limitation.
- Keyed `IAgent` propagation.
- No partial completion event.

#### Acceptance criteria

- Buffered and streaming paths classify timeout identically.
- Caller cancellation behavior remains unchanged.
- All existing tests still pass.
- Public API compatibility review passes.

#### Compatibility risks

- A new default timeout could change previously unbounded runs.
- Adding fields to existing positional records would be breaking and is prohibited.

### Increment 2 — Provider failure and retry-boundary hardening

Implementation status: implemented locally on 2026-10-20. Verification details are recorded in `docs/CURRENT_STATUS.md`.

#### Objective

Make retry ownership and ambiguous failures explicit without adding runtime retries.

#### Projects and files likely to change

- `src/AgentForge.Abstractions/Exceptions.cs`.
- Provider exception mappers.
- OpenAI and Microsoft.Extensions.AI adapter tests.
- `docs/architecture.md`.
- README.

#### Public API changes

Prefer documentation and additive classification only. Do not replace `ModelProviderException`.

#### Failure and cancellation semantics

- Stable transient and permanent mappings.
- Unknown remains non-transient.
- `IsTransient` remains advisory.
- Failures after stream output are classified as ambiguous and never replayed.
- Tools are never retried.
- Provider-owned and externally supplied client retry ownership is documented.

#### Test requirements

- Every provider error kind.
- Transient and permanent classification.
- Sanitized `RetryAfter` and request-ID handling.
- No second adapter invocation.
- Failure before first delta.
- Failure after partial output.
- Cancellation never becomes a transient failure.
- Caller-supplied client behavior remains caller-owned.

#### Acceptance criteria

- AgentForge performs exactly one provider invocation per engine attempt.
- No tool is automatically retried.
- Failure messages contain no response body or credential.
- Retry ownership documentation is unambiguous.

#### Compatibility risks

- Reclassifying existing errors can affect consumer branching and requires explicit contract review.

### Increment 3 — Input, output, and tool-payload bounds

#### Objective

Bound all provider-neutral content retained or advanced by Core.

#### Projects and files likely to change

- A new resource-policy type.
- `AgentExecutionEngine.cs`.
- `AgentRuntime.cs`.
- Message and tool JSON sizing helpers.
- Hosting builder and dependency injection.
- Core, Tools, and Hosting tests.
- README and architecture documentation.

#### Public API changes

Additive policy or options types and builder methods. Existing constructors remain.

#### Failure and cancellation semantics

- Limits fail with stable policy codes.
- Whole tool batches are rejected before side effects.
- A streamed delta crossing the ceiling is not emitted.
- No terminal completion follows a limit failure.

#### Test requirements

- Oversized initial messages.
- Oversized accumulated transcript.
- Oversized buffered response.
- Exact-boundary response.
- Streaming cumulative overflow across small deltas.
- Oversized tool arguments in a multi-call batch with zero side effects.
- Oversized tool result.
- Checked counter overflow.
- Concurrent runs with different policies.

#### Acceptance criteria

- All enforcement occurs before the next avoidable external invocation.
- Rejected tool batches have no partial side effects.
- Buffered and streaming terminal classification matches.
- Limits are documented in precise characters or UTF-8 bytes.

#### Compatibility risks

- Finite defaults may reject workloads accepted by `0.2.0-preview.1`.
- Character versus byte semantics must not change after publication.

### Increment 4 — Provider processing and streaming bounds

#### Objective

Enforce limits as early as possible inside OpenAI and Microsoft.Extensions.AI adapters and make buffering explicitly bounded.

#### Projects and files likely to change

- OpenAI request mapper, response mapper, stream accumulator, and options.
- Microsoft.Extensions.AI response mapper and stream accumulator.
- `AgentRuntime.cs`.
- Provider adapter tests.

#### Public API changes

Potential additive provider options only. No mandatory provider changes.

#### Failure and cancellation semantics

- Adapter-detected oversized data becomes a sanitized provider protocol or resource failure that Core maps consistently.
- Consumer disposal and caller cancellation retain priority.
- No replay occurs after any stream event.

#### Test requirements

- Excessive delta size.
- Excessive cumulative text.
- Excessive function-argument accumulation.
- Excessive event count.
- Fragmented transport input.
- Cancellation during bounded accumulation.
- Capacity-one runtime channel behavior.
- Enumerator disposal after rejection.

#### Acceptance criteria

- No unbounded runtime channel remains.
- Adapter builders check before growth beyond configured limits.
- Documentation discloses SDK pre-buffering limitations.

#### Compatibility risks

- Provider-specific limits must not diverge from Core semantics.
- Provider option changes must not leak SDK types into Abstractions.

### Increment 5 — Observed token-budget policy

Implementation status: completed locally on 2026-10-21. The approved fail-closed behavior is used whenever any observed-token budget is configured.

#### Objective

Stop further model calls when reported usage exhausts a configured budget.

#### Projects and files likely to change

- Resource-policy type.
- `AgentExecutionEngine.cs`.
- Provider usage mapping tests.
- Core tests and documentation.

#### Public API changes

Add an optional token budget without changing existing constructors.

#### Failure and cancellation semantics

- Check before each later model invocation.
- Over-budget reported usage ends the run with `TokenBudgetExceeded`.
- Do not claim hard enforcement within a completed provider call.
- Missing usage follows the founder-approved fail-closed or advisory rule.

#### Test requirements

- Budget reached exactly.
- Budget exceeded in one response.
- Cumulative exhaustion across tool turns.
- Missing usage.
- Partial usage fields rejected by adapters.
- Arithmetic overflow.
- No next model invocation after exhaustion.

#### Acceptance criteria

- Documentation explicitly distinguishes observed usage from provider generation limits.
- No provider without usage is described as hard-budgeted.
- Existing result usage remains unchanged.

#### Compatibility risks

- Fail-closed missing-usage behavior could reject providers that previously worked.
- This policy should be opt-in unless approved otherwise.

### Increment 6 — Activity instrumentation

#### Objective

Add exporter-neutral execution, model, and tool spans.

#### Projects and files likely to change

- New internal diagnostics and instrumentation files in Core.
- `AgentExecutionEngine.cs`.
- `AgentRuntime.cs`.
- Core tests.
- Architecture and diagnostics documentation.

#### Public API changes

No required public API. Expose stable source and name constants only if external subscribers need compile-time access.

#### Failure and cancellation semantics

Every span ends exactly once with a stable outcome, including cancellation and timeout.

#### Test requirements

- Parent trace propagation.
- Nested run to model/tool span structure.
- Buffered and streaming span parity.
- Success, provider failure, tool failure, rejection, cancellation, and timeout.
- Instrumentation-disabled fast path.
- No sensitive strings in names, tags, events, or exception data.

#### Acceptance criteria

- No exporter dependency.
- No prompts, responses, arguments, results, IDs, or exception messages are recorded by default.
- Telemetry listener behavior cannot change runtime behavior.

#### Compatibility risks

- Activity names and tags become observability compatibility surfaces and require documentation and versioning discipline.

### Increment 7 — Metrics and structured terminal diagnostics

#### Objective

Add low-cardinality counters and histograms plus complete sanitized lifecycle logs.

#### Projects and files likely to change

- Core instrumentation files.
- `AgentExecutionEngine.cs`.
- Logging tests.
- Metrics tests.
- README and observability reference.

#### Public API changes

No mandatory change.

#### Failure and cancellation semantics

Metrics and logs use the same internal classifier as activities.

#### Test requirements

- Exact counter increments.
- Duration histograms with deterministic or broadly asserted values.
- Token metrics only when usage exists.
- Stable cancellation versus timeout outcomes.
- No high-cardinality tags.
- Sensitive-data exclusion from logs and metrics.
- Telemetry absent or listener-disabled behavior.

#### Acceptance criteria

- Every started run produces exactly one terminal run outcome.
- Model and tool invocation metrics reflect actual invocations, not rejected attempts.
- No execution, agent, model, tool, tenant, provider-request, or call IDs appear as metric dimensions.
- Instrumentation does not affect execution results.

#### Compatibility risks

- Metric names, units, and tag sets become published contracts.
- Existing agent and model logging should not be removed without founder approval.

### Increment 8 — Documentation, compatibility, and full regression gate

#### Objective

Finalize Phase 3 contracts and prove compatibility with the preview integration surface.

#### Projects and files likely to change

- `docs/architecture.md`.
- New resilience and observability documentation.
- README.
- Packaging or API verification tests and baselines.
- All relevant test projects.

#### Public API changes

Freeze approved additions and document them with XML comments.

#### Failure and cancellation semantics

No new semantics are introduced. This increment verifies that all approved behavior is consistent and documented.

#### Test requirements

- Full existing suite.
- Public API and binary compatibility baseline.
- External package consumer using `AgentForge.Hosting`.
- Keyed `IAgent` registration and resolution.
- Buffered and streaming examples.
- No live provider calls.

#### Acceptance criteria

- Release build has zero warnings.
- All deterministic tests pass.
- A clean external consumer compiles against the new packages using the current Hosting API.
- No telemetry exporter is transitively required.
- No YFlow.NET changes are necessary.

#### Compatibility risks

The AgentForge repository cannot prove YFlow integration without YFlow source or an explicit compatibility fixture. Founder or YFlow CI confirmation remains necessary before release.

## 5. Public API compatibility assessment

Safe additive changes are:

- New enum values appended to `AgentExecutionErrorCode`.
- New immutable options or policy types.
- New `AddAgentForge` overloads.
- New builder methods.
- New `AgentRuntime` overloads while retaining the existing constructor.
- Public constants for instrumentation names if needed.

Changes to avoid are:

- Changing `IAgent`, `IAgentRuntime`, or `IStreamingAgentRuntime`.
- Adding parameters to existing positional records.
- Changing existing constructor signatures.
- Renaming current error codes.
- Changing keyed agent service keys or lifetimes.
- Introducing an unkeyed default agent.
- Requiring an OpenTelemetry SDK or exporter package.
- Adding retry behavior to existing calls.
- Changing provisional streaming events into authoritative data.
- Parallelizing tools.
- Exposing provider SDK types through shared contracts.

Behavioral compatibility concerns are:

- A non-null default timeout or size ceiling changes accepted workloads.
- New error classification may affect consumers that branch on error codes.
- Removing agent and model IDs from current logs changes operational behavior, though not public API.
- Fail-closed token budgets would affect providers that omit usage.

A public API compatibility baseline should be generated from `0.2.0-preview.1` before implementation and checked in CI.

## 6. Deterministic testing strategy

### Timeout versus cancellation

Use `TaskCompletionSource` gates and a production delay abstraction or `TimeProvider`, never arbitrary sleeps:

- Caller cancels while a provider is blocked: `OperationCanceledException`.
- Runtime deadline fires while a provider is blocked: `ExecutionTimedOut`.
- Caller cancellation and deadline race: caller cancellation wins.
- Repeat the matrix for tool invocation and streaming `MoveNextAsync`.
- A provider ignores cancellation and returns: runtime still reports the correct cancellation or timeout.
- Early streaming disposal cancels execution without reporting failure to the disposer.

### Provider transient and permanent failures

Test all stable mappings:

- Authentication, authorization, invalid request, and model-not-found failures are permanent.
- Request timeout, rate limiting, service unavailability, and transport failures are potentially transient.
- Protocol and unknown failures are non-transient.
- `RetryAfter` is accepted only when valid and bounded.
- Provider request IDs are accepted only when sanitized.
- Body, prompt, credential, and raw exception message are excluded from AgentForge diagnostics.

### Retry boundaries

- Exactly one provider call per turn.
- No automatic retry for transient buffered failures.
- No retry before the first streaming delta.
- No retry after partial streaming output.
- No tool retry after failure.
- No retry after timeout.
- No multiplicative retry introduced by Core.
- Caller-owned SDK behavior remains outside AgentForge guarantees.

### Streaming cancellation and limits

- Cancellation before provider enumeration.
- Cancellation while waiting for the next event.
- Cancellation while publishing a delta.
- Early disposal.
- Empty, duplicate, missing-terminal, and post-terminal events.
- Cumulative output limit across multiple deltas.
- A crossing delta is withheld.
- Event-count limit.
- Tool-argument accumulation limit.
- Capacity-one channel backpressure.

### Tool execution and resource limits

- Existing model and tool-call boundaries.
- Input text at, below, and above the limit.
- Tool argument and result byte limits with multibyte UTF-8.
- Multi-tool oversized batch produces no side effects.
- Earlier successful tool remains observable when a later invocation, rather than prevalidation, fails.
- Transcript growth is checked before another model call.
- Checked arithmetic overflow.
- Concurrent runs do not share counters, tokens, deadlines, or telemetry state.

### Token budgets

- No usage and no configured budget succeeds as today.
- Exact budget.
- Exceeded budget.
- Cumulative usage across model turns.
- No subsequent provider call after exhaustion.
- Missing usage under the approved policy.
- Buffered and streaming parity.

### Telemetry

Use `ActivityListener` and `MeterListener` directly:

- Ambient parent is preserved.
- One run span exists per execution.
- One model span exists per actual provider call.
- One tool span exists per actual invocation.
- Parent-child hierarchy is correct.
- Exactly one terminal outcome is recorded.
- Cancellation and timeout are distinct.
- Metric counters and histograms record the expected bounded tags.
- Token counters record reported usage only.
- Telemetry is not emitted for rejected operations that never crossed the relevant boundary.
- Prompts, responses, arguments, results, credentials, endpoints, arbitrary IDs, and secret sentinel values never appear.
- Metric attributes remain low-cardinality.
- No listener changes runtime behavior.

### Existing Phase 1 and Phase 2 regression gates

Retain all current coverage:

- Successful no-tool runs.
- Tool round trips and ordering.
- Authorization and registration.
- Atomic batch validation.
- Model and tool limits.
- Invalid responses and finish reasons.
- Provider and tool cause preservation.
- JSON ownership.
- Concurrent isolation.
- Keyed agents.
- OpenAI and Microsoft.Extensions.AI mappings.
- Typed-tool validation.
- Live-verification gate safety.

No test may require network access, credentials, arbitrary delay, or execution-order luck.

## 7. Phase 3 acceptance criteria

Phase 3 is complete only when:

1. Caller cancellation and AgentForge timeout have distinct, documented, deterministic behavior.
2. Timeout behavior is identical for buffered and streaming execution.
3. Provider and tool tokens are propagated across every asynchronous boundary.
4. AgentForge Core performs no automatic durable retry.
5. Tools are never automatically retried.
6. Transient provider classification is advisory and ambiguous outcomes are documented.
7. Input, output, streaming, tool-argument, tool-result, event, model-call, and tool-call paths have explicit bounds.
8. An over-limit tool batch causes no partial side effects.
9. Streaming never emits a delta that crosses the configured output limit.
10. Token budgets are described as observed-usage policies unless provider guarantees make stronger enforcement possible.
11. Every run, model invocation, and tool invocation can emit an exporter-neutral activity.
12. Execution, model, and tool counts and durations are available through `Meter`.
13. Cancellation, timeout, failure, and limit outcomes are distinguishable in logs, spans, and metrics.
14. Metric dimensions are closed and low-cardinality.
15. Prompts, responses, tool arguments and results, credentials, provider bodies, tenant data, and raw exception messages are excluded by default.
16. Ambient trace parentage is preserved.
17. No OpenTelemetry exporter is mandatory or transitively required.
18. Existing Hosting APIs and keyed-agent resolution remain source and binary compatible.
19. A clean external consumer using the current `AgentForge.Hosting` pattern compiles and runs without changes.
20. Release build succeeds with zero warnings.
21. All existing and new deterministic tests pass.
22. No live OpenAI call, package publication, commit, push, or YFlow.NET modification is performed as part of implementation verification.

## 8. Architectural decisions requiring founder approval

1. **Retry policy:** approve the recommendation that Core performs no automatic provider retry in Phase 3, with YFlow.NET retaining durable retry ownership.
2. **Default execution timeout:** decide whether new registrations receive a finite default deadline or remain unlimited until explicitly configured. A finite default improves safety but changes behavior.
3. **Default resource ceilings:** approve exact input, output, tool-payload, and stream-event defaults. These values become compatibility and operational contracts.
4. **Policy location:** decide whether resilience and resource policy is global runtime configuration, per-agent configuration, or global defaults with per-agent overrides. Per-agent overrides are more flexible but expand the public surface.
5. **Missing token usage:** choose fail closed when `MaxTotalTokens` is configured but the provider reports no usage, or treat the budget as advisory. Fail closed is recommended.
6. **Timeout exception contract:** approve `AgentExecutionException` with `ExecutionTimedOut` rather than surfacing timeout as `OperationCanceledException` or `TimeoutException`.
7. **Existing log identifiers:** approve removing agent and model identifiers from default logs, or explicitly classify them as safe opt-in metadata. They are uncontrolled strings and may contain tenant-sensitive information.
8. **Telemetry naming contract:** approve the proposed `AgentForge` source and meter plus `agentforge.*` activity and metric names before they become public operational contracts.
9. **Instrumentation location:** approve instrumentation directly in Core using BCL primitives rather than creating an empty or exporter-coupled `AgentForge.Observability` package.
10. **Provider-native output controls:** decide whether provider-specific maximum-output-token settings are part of Phase 3 or a later adapter increment. Such settings supplement but do not replace Core bounds.
11. **Compatibility verification:** approve requiring YFlow CI or a minimal external compatibility fixture before the next preview because the YFlow repository is not available here for direct verification.

`PHASE 3 IMPLEMENTATION COMPLETE — READY FOR COORDINATED PRERELEASE COMMIT`
