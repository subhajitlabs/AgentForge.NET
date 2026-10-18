# AgentForge.NET Engineering Guide

This file applies to the entire repository. More specific `AGENTS.md` files may add local rules but must not weaken these constraints without an explicit architecture decision.

## Product boundary

- Build an in-process, provider-neutral AI agent runtime for .NET. Do not introduce services or distributed infrastructure without an approved requirement.
- Follow `docs/architecture.md`. If implementation pressure conflicts with it, update the document and obtain agreement before changing a public boundary.
- Implement only the active milestone. Do not add placeholder projects, speculative interfaces, or unused extension points.

## Platform and language

- Target .NET 10 and use the repository-pinned SDK.
- Enable nullable reference types, implicit usings, deterministic builds, and warnings as errors.
- Prefer current idiomatic C# where it improves clarity; avoid clever syntax that obscures behavior.
- All asynchronous public APIs accept an optional trailing `CancellationToken` and use the `Async` suffix.
- Do not use sync-over-async (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`) in product code.

## Architecture and dependencies

- Maintain dependency direction: adapters/Hosting -> Abstractions; Core -> Abstractions. Core must never reference a concrete provider, persistence, or telemetry adapter.
- Keep provider SDK types out of AgentForge public contracts.
- Abstractions contains contracts and immutable domain values, not orchestration implementations.
- Core owns execution policy and lifecycle. Hosting owns DI registration only.
- Prefer composition, sealed types, small interfaces, constructor injection, and explicit state.
- Do not use service locator patterns, ambient mutable state, global registries, or static caches.
- Add a NuGet dependency only when the BCL or existing dependencies cannot reasonably meet an active requirement. Centralize package versions.
- Never suppress warnings globally to make a build pass. Fix or narrowly document the reason.

## Public API design

- Use strongly typed IDs/value objects where values from different domains could otherwise be confused.
- Use immutable records or read-only snapshots at public boundaries; do not retain caller-owned mutable collections.
- Validate arguments at the boundary and use precise parameter names.
- Document all public types and members with XML comments.
- Treat public APIs and serialized representations as compatibility surfaces. Add contract/API review before changing them.
- Do not expose implementation-specific exceptions. Preserve root causes as inner exceptions and stable AgentForge error codes where callers need branching behavior.

## Runtime behavior

- Every execution path must be bounded by explicit limits or a cancellation token.
- Model-call and tool-call limits must both be positive and enforced before invocation.
- Propagate the caller's cancellation token to every provider/tool operation. Do not wrap cancellation caused by that token as a failure.
- Tool execution is deny-by-default: a tool must be registered and explicitly allowed for the agent.
- Treat model output and tool arguments as untrusted input.
- Keep milestone 1 tool execution sequential and deterministic.
- Prevalidate every tool-call batch for authorization, registration, unique call IDs, valid JSON, and remaining budget before executing any tool.
- Never automatically retry a side-effecting tool.
- Never log prompts, message bodies, tool arguments/results, credentials, tokens, or connection strings by default.

## Errors and logging

- Fail fast for invalid configuration and malformed provider responses.
- Add context when crossing provider/tool boundaries while preserving the original exception.
- Use structured logging templates with stable property names. Log identifiers, durations, counts, and outcomes rather than content.
- Do not catch `Exception` unless adding boundary context, performing safe cleanup, or translating at a defined boundary. Never silently swallow failures.

## Testing

- Use xUnit and deterministic fakes. Tests must not require network access, real credentials, wall-clock delays, or execution-order luck.
- Follow Arrange/Act/Assert when it helps readability; name tests `Method_Condition_ExpectedResult`.
- Test observable behavior rather than private implementation details.
- Required lifecycle coverage includes success without tools, tool round-trips, ordering, limit enforcement, cancellation at each async boundary, provider/tool failure propagation, invalid provider responses, tool authorization, and concurrent run isolation.
- Cancellation tests must coordinate with signals (for example, `TaskCompletionSource`), not arbitrary sleeps.
- For limit failures, assert that a rejected batch causes no partial tool side effects.
- Every bug fix includes a regression test when practical.

## Repository hygiene

- Never commit secrets. Use environment variables, user secrets, or ignored local configuration for credentials.
- Do not commit build outputs (`bin/`, `obj/`, `TestResults/`) or IDE/user-specific files.
- Keep changes scoped; do not reformat unrelated files.
- Prefer one type per file when it improves discovery; match filenames to primary public types.
- Record significant architectural choices in `docs/` before or with their implementation.

## Definition of done

- The active milestone's acceptance criteria are met.
- `dotnet build` succeeds with zero warnings.
- `dotnet test` succeeds with all tests passing.
- Public APIs have XML documentation and relevant usage docs are updated.
- No secrets, generated artifacts, unused code, or speculative abstractions were introduced.
- The completion report lists commands run, results, known limitations, and next steps. Never claim a build or test passed without running it.
