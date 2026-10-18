# AgentForge.NET v0.2.0-preview.1 Release-Readiness Audit

Audit date: 2026-10-18 (Asia/Calcutta)

Audited commit: `f326b9030d1da5cd32e43d15910d72831577399d`

Recommendation: **NO-GO — release-blocking NuGet packaging defects remain.**

## Executive summary

The Phase 2 runtime implementation is technically coherent and its offline verification is healthy. The Release build completed with zero warnings and errors; all 203 deterministic tests passed; all credential-free samples ran; the package dependency graph restored in an external .NET 10 project; and a named-agent run executed from locally packed binaries without project references. The audit found no Critical security issue, no duplicated execution loop, no provider SDK leakage into provider-neutral contracts, and no authorization, limit, cancellation, or keyed-agent isolation regression in the inspected implementation and tests.

The repository is nevertheless not ready to publish as `v0.2.0-preview.1`. Every intended library currently packs as version `1.0.0` with placeholder/missing NuGet metadata, and a solution-level pack emits all four sample executables as packages. Package artifacts also omit the promised README, XML documentation, symbols, and SourceLink material. These are confirmed, reproducible release blockers rather than polish concerns.

The reported 4/4 live OpenAI result was not rerun in this audit, as directed. Repository documentation currently says the corrected typed-tool scenario has not been rerun, so the repository does not contain internally consistent evidence for the reported 4/4 baseline. This is a documentation/evidence issue, not proof that the adapter is defective.

## Repository and working-tree state

`git rev-parse HEAD` returned:

```text
f326b9030d1da5cd32e43d15910d72831577399d
```

The pre-audit working tree was dirty. These user-owned modifications were present and were included in the audited source state:

```text
 M README.md
 M docs/CURRENT_STATUS.md
 M docs/PHASE2_PROPOSAL.md
 M samples/AgentForge.Samples.Console/Program.cs
 M src/AgentForge.Providers.OpenAI/Internal/OpenAIRequestMapper.cs
 M tests/AgentForge.LiveVerification/Program.cs
 M tests/AgentForge.Providers.OpenAI.Tests/OpenAIProviderTests.cs
 M tests/AgentForge.Tools.Tests/TypedToolTests.cs
```

This audit added only `docs/PHASE2_RELEASE_AUDIT.md`. No branch, commit, tag, release, publish, push, product-code change, or live provider call was made.

## Findings

### AF-REL-001 — High — package identity and mandatory release metadata are incorrect

- **Location:** `Directory.Build.props:1`; all six `src/*/*.csproj` files, beginning at line 1.
- **Evidence:** No project or central build file defines `Version`, `PackageVersion`, release authors, descriptions, repository URL, license expression/file, or package README. Packing produced `AgentForge.*.1.0.0.nupkg`. Each nuspec used the project name as author, `Package Description` as description, and had no license or README metadata. The repository element contained a commit but no URL.
- **Reproduction:** `dotnet pack src/AgentForge.Abstractions/AgentForge.Abstractions.csproj -c Release --no-build -o <temp>` and inspect the filename and nuspec. The same result occurs for all six library projects.
- **Production impact:** Publishing would create the wrong stable-looking `1.0.0` identities rather than `0.2.0-preview.1`, with incomplete provenance and licensing. Package-to-package dependencies would also be pinned to the unintended `1.0.0` versions.
- **Minimal fix:** Centrally define `Version`/`PackageVersion` as `0.2.0-preview.1`, authors, meaningful per-package descriptions, `RepositoryUrl`, repository type, and license metadata. Include the repository README as the declared package README. Repack and re-inspect every nuspec.
- **Blocks release:** **Yes.**

### AF-REL-002 — High — solution packing accidentally produces sample packages

- **Location:** `samples/AgentForge.Samples.Console/AgentForge.Samples.Console.csproj:1`; `samples/AgentForge.Samples.Streaming/AgentForge.Samples.Streaming.csproj:1`; `samples/AgentForge.Samples.OpenAI/AgentForge.Samples.OpenAI.csproj:1`; `samples/AgentForge.Samples.MicrosoftExtensionsAI/AgentForge.Samples.MicrosoftExtensionsAI.csproj:1`.
- **Evidence:** The sample projects do not set `IsPackable` to `false`. `dotnet pack AgentForge.slnx -c Release --no-build -o <temp>` produced four `AgentForge.Samples.*.1.0.0.nupkg` files in addition to the six intended library packages. Test and live-verification projects correctly set `IsPackable=false` and did not produce packages.
- **Reproduction:** Run the solution-level pack command above and enumerate `*.nupkg`.
- **Production impact:** A broad release pack/upload glob can publish unsupported sample executables, pollute the package namespace, and create a larger accidental compatibility surface.
- **Minimal fix:** Add `<IsPackable>false</IsPackable>` to every sample project and add a packaging verification that asserts the exact six-package allow-list.
- **Blocks release:** **Yes.**

### AF-REL-003 — Medium — release artifacts omit XML documentation, symbols, and SourceLink

- **Location:** `Directory.Build.props:2-9` and all publishable project files.
- **Evidence:** None of the six library nupkgs contained XML documentation. No `.snupkg` was generated. The packages contained only the nuspec, package metadata, and `lib/net10.0/*.dll`; no SourceLink package/configuration was present. Pack emitted a missing-readme warning for every project.
- **Reproduction:** Pack the six `src` projects and list archive entries. Only the DLL is present under `lib/net10.0`.
- **Production impact:** Consumers lose IntelliSense documentation and source/debug traceability; this violates the requested release requirements and weakens preview-package supportability.
- **Minimal fix:** Enable `GenerateDocumentationFile`, symbol-package generation (`snupkg`), repository/source embedding and SourceLink using the supported .NET SDK configuration; include the README; then verify archive contents and source mapping.
- **Blocks release:** **Yes**, as part of the packaging correction required for this release candidate.

### AF-DOC-001 — Medium — live-verification evidence is internally inconsistent

- **Location:** `README.md:94`; `docs/CURRENT_STATUS.md:17`.
- **Evidence:** README says live verification “has not been executed.” Current status says a prior run exposed a dotted OpenAI function-name failure and that the corrected scenario has not been rerun. This conflicts with the supplied release baseline of 4/4 passing after the underscore-name correction. The audit intentionally did not make a paid live call.
- **Reproduction:** Read the cited lines and compare them with the release baseline.
- **Production impact:** Reviewers cannot establish from repository evidence which source state was live-verified, and users receive contradictory compatibility claims.
- **Minimal fix:** Record the exact post-fix live run date, commit/tree state, model, four sanitized outcomes, and limitations, or correct the claimed baseline if such a run did not occur. Keep the pre-cancelled-token limitation explicit.
- **Blocks release:** **Yes**, until the evidence is reconciled; no new live run is necessarily required if trustworthy post-fix evidence already exists.

### AF-DOC-002 — Medium — installation and package-level quick starts are incomplete

- **Location:** `README.md:18-38` and provider sections beginning at `README.md:75`.
- **Evidence:** README describes build commands and API fragments but does not give `dotnet add package` commands or identify which packages each scenario requires. The main quick-start snippet depends on undeclared surrounding variables/usings (`modelClient`, `cancellationToken`). A complete external equivalent did compile once the necessary packages and setup were supplied.
- **Reproduction:** Attempt to follow README from a blank project without repository project references.
- **Production impact:** NuGet consumers cannot reproduce the documented start literally and may omit a required package.
- **Minimal fix:** Add versioned installation commands and one complete, compilable offline named-agent example. Add complete OpenAI, typed-tool, streaming, and MEAI package/usings guidance.
- **Blocks release:** **Yes**, as a minor correction accompanying the packaging blockers.

### AF-SEC-001 — Medium — execution bounds do not include provider output or payload size

- **Location:** `src/AgentForge.Providers.OpenAI/Internal/OpenAIRequestMapper.cs:14-20`; `src/AgentForge.Providers.OpenAI/Internal/OpenAIResponseMapper.cs:27-59`; `src/AgentForge.Providers.OpenAI/Internal/OpenAIStreamAccumulator.cs:89-110`.
- **Evidence:** Requests disable storage and parallel tools and runs bound model/tool call counts, but the public adapter exposes no maximum-output-token control. Text and streamed argument accumulation have no AgentForge byte/character cap. README already discloses the output-token limitation.
- **Reproduction:** Configure a provider/model to return a very large response; AgentForge accumulates it until provider/transport/caller limits intervene.
- **Production impact:** A trusted provider, compromised compatible endpoint, or unexpectedly large response can cause elevated memory/cost. This is a hardening gap, not an observed authorization bypass or remotely demonstrated exploit.
- **Minimal fix:** For this preview, prominently document that callers must impose cancellation/timeouts and provider-side output limits where available. Plan a bounded public output/payload option through architecture review rather than redesigning it during release stabilization.
- **Blocks release:** **No** for the preview if accurately documented.

### AF-DEP-001 — Low — direct Microsoft.Extensions versions are not aligned

- **Location:** `Directory.Packages.props:5-8`.
- **Evidence:** DI packages are `10.0.9`, MEAI abstractions are `10.11.0`, but `Microsoft.Extensions.Logging.Abstractions` is pinned to `10.0.0`. Resolution succeeded without downgrade/conflict warnings, and consuming graphs commonly resolved a higher transitive logging version where applicable.
- **Reproduction:** `dotnet list AgentForge.slnx package --include-transitive`.
- **Production impact:** No current failure; version skew increases graph variability and maintenance friction.
- **Minimal fix:** Review and intentionally align the Microsoft.Extensions patch/minor policy before release, or document why the lower direct minimum is deliberate.
- **Blocks release:** **No.**

### AF-DEP-002 — Low — test projects use packages reported as deprecated

- **Location:** `Directory.Packages.props:11-12` and test project package references.
- **Evidence:** `dotnet list AgentForge.slnx package --deprecated --include-transitive` reports xUnit `2.9.3` and its v2 components as Legacy, recommending xUnit v3. No production package is reported deprecated.
- **Reproduction:** Run the command above with nuget.org available.
- **Production impact:** Test-infrastructure maintenance concern only; it does not flow into library package dependencies.
- **Minimal fix:** Evaluate an xUnit v3 migration separately; do not destabilize this preview solely for that migration.
- **Blocks release:** **No.**

## Architecture and public API audit

- `AgentForge.Abstractions` contains provider-neutral contracts and BCL types only. OpenAI SDK types remain inside `AgentForge.Providers.OpenAI`; MEAI types remain inside its adapter.
- `AgentExecutionEngine` is the single policy loop used by buffered and streaming execution. `AgentRuntime.StreamAsync` adapts provider events into the same engine rather than duplicating authorization, budgets, transcript mutation, usage accumulation, or tool execution.
- `RegisteredAgent` is a thin facade: `RunAsync` and `StreamAsync` create a fresh request and delegate to `IAgentRuntime`/`IStreamingAgentRuntime`.
- Streaming exposes provisional `AssistantTextDelta` events and exactly one authoritative `AgentRunCompleted` on success. Missing, duplicate, contradictory, or post-terminal provider events fail. Early consumer disposal links cancellation and awaits producer termination.
- Tool execution is deny-by-default. Registration and per-agent `AllowTool` are separate. Each batch is checked for IDs, duplicate IDs across the run, authorization, registration, remaining budget, and typed argument validity before any side effect. Execution stays sequential.
- Model and tool budgets are positive-validated and enforced before invocation. Caller cancellation is checked and propagated at provider/tool/enumerator boundaries without wrapping caller cancellation.
- Public `JsonElement` inputs are cloned on construction, so values do not retain caller-owned `JsonDocument` lifetime. Collections at public boundaries are copied/read-only or frozen.
- Named-agent registrations are keyed singletons with immutable definitions. Duplicate keys are rejected at registration; unknown allowed tools and model-client cardinality fail on resolution before a model call. Tests cover fresh-run isolation and concurrent use.
- Provider-owned OpenAI configuration snapshots mutable options immediately. Endpoint validation requires HTTPS except explicit loopback HTTP, rejects URI user-info/fragments, disables SDK retries, disables provider storage, and disables parallel tool calls.
- Caller-owned OpenAI/MEAI factories create singleton adapters. Their documentation makes ownership explicit, but callers must not resolve a scoped client from these singleton factories; DI scope validation remains advisable.
- Public types and members are generally XML-commented in source. No provider SDK type appears in the provider-neutral API. No speculative Phase 3 surface was found.
- API polish concerns that are not confirmed defects: several public record/value constructors defer semantic validation until runtime, and `JsonValueSnapshot` converts disposed/invalid elements to `Undefined` rather than failing at the constructor boundary. Existing runtime/registry validation prevents unsafe execution, but future API review should consider earlier, more precise argument failures.

## Security audit

No Critical or High exploitable security defect was confirmed.

- Repository scanning and manual inspection found no committed credential value. Environment-variable names and test sentinel strings are not credentials.
- Runtime logs contain execution, agent/model identifiers, counts, and outcomes; prompts, bodies, tool arguments/results, credentials, and connection strings are not logged by product code.
- AgentForge-owned exception messages are sanitized. Provider/tool root causes are intentionally preserved as inner exceptions; full exception logging can therefore expose provider- or application-owned sensitive data. This is documented behavior and requires an application redaction policy.
- OpenAI request mapping sends the full explicit transcript, `store=false`, no provider conversation/previous-response state, only authorized tool descriptors, and `parallel_tool_calls=false`.
- The OpenAI mapper validates provider function-name syntax before transport. Response mapping rejects unsupported/contradictory states and malformed JSON. Streaming reconstructs tool arguments privately and requires terminal consistency.
- MEAI mapping rejects unsupported roles/content and contradictory finish reasons. The adapter requires a raw client and documents that automatic tool-invocation middleware is unsupported; AgentForge remains the sole execution boundary.
- Typed tools freeze serializer options, disallow unknown members, reject custom resolvers/converters outside the supported enum converter, restrict schema depth/properties and contract shapes, validate complete batches atomically, and deserialize only after validation.
- No automatic retry is present. The AgentForge-owned OpenAI SDK client sets retry count to zero. A caller-owned `ResponsesClient` can have caller-configured retries, which the public constructor documentation assigns to the caller.
- Endpoint configuration is HTTPS-only except explicitly enabled HTTP loopback and rejects embedded user information and fragments.
- Live verification requires all four gates before provider construction; output reporting uses fixed classifications and does not print credential or provider exception text. Its cancellation scenario uses an already-cancelled token and does not prove cancellation of accepted remote work.
- Remaining resource-exhaustion limitations are recorded in AF-SEC-001. Tool implementations themselves are trusted in-process application code and are not sandboxed.

## NuGet packaging results

Intended library packages and inspected outcomes:

| Package ID | Actual version | TFM | Main dependencies | Result |
|---|---:|---|---|---|
| AgentForge.Abstractions | 1.0.0 | net10.0 | none | Wrong version; metadata/artifacts incomplete |
| AgentForge.Core | 1.0.0 | net10.0 | Abstractions 1.0.0; Logging.Abstractions 10.0.0 | Graph correct; metadata/artifacts incomplete |
| AgentForge.Hosting | 1.0.0 | net10.0 | Abstractions/Core 1.0.0; DI 10.0.9 | Graph correct; metadata/artifacts incomplete |
| AgentForge.Tools | 1.0.0 | net10.0 | Abstractions 1.0.0 | Graph correct; metadata/artifacts incomplete |
| AgentForge.Providers.OpenAI | 1.0.0 | net10.0 | Abstractions 1.0.0; DI.Abstractions 10.0.9; OpenAI 2.14.0 | Graph correct; metadata/artifacts incomplete |
| AgentForge.Adapters.MicrosoftExtensionsAI | 1.0.0 | net10.0 | Abstractions 1.0.0; MEAI.Abstractions 10.11.0; DI.Abstractions 10.0.9 | Graph correct; metadata/artifacts incomplete |

The OpenAI experimental warning suppression is narrowly scoped to `AgentForge.Providers.OpenAI.csproj` as `OPENAI001`, with an upgrade-review comment. Deterministic and CI build properties are central and active. Tests/live verification were not packed. Samples were packed, as AF-REL-002 records.

## Dependency review

Central package management is enabled. Restore showed no downgrade or conflict warning. The resolved production graph contained the expected Microsoft.Extensions packages, OpenAI `2.14.0`, System.ClientModel `1.15.0`, and System.Memory.Data `10.0.9`; no unexpected prerelease package was resolved.

`dotnet list AgentForge.slnx package --vulnerable --include-transitive` successfully queried nuget.org and reported no known vulnerable package for every project. This is point-in-time advisory evidence, not a guarantee of vulnerability absence. The deprecated-package command reported only xUnit v2 test dependencies, as AF-DEP-002 describes.

## Documentation verification

- Named-agent, typed-tool allow-list, streaming terminal event, OpenAI configuration, MEAI raw-client ownership, cancellation, independent model/tool budgets, live gates, no retries, cost/output-limit caveat, and security limitations generally match implementation.
- The deterministic calculator, streaming, MEAI, and gated OpenAI samples all executed as documented. The OpenAI sample exited before provider construction because credentials were absent.
- The README does not claim production stability, durability, memory, retries, multi-agent orchestration, or other Phase 3 capabilities.
- Installation/package guidance and live-evidence wording require correction under AF-DOC-001/002.

## Build, test, sample, and hygiene results

Commands run from the repository root:

```text
dotnet restore AgentForge.slnx
  PASS — all projects restored/up-to-date.

dotnet build AgentForge.slnx --configuration Release --no-restore
  PASS — 0 warnings, 0 errors.

dotnet test AgentForge.slnx --configuration Release --no-build
  PASS — 203/203 tests:
    Core 104; Tools 31; OpenAI 33; MEAI 23; live guards 12.

git diff --check
  PASS — no whitespace errors; Git emitted line-ending conversion notices for existing edits.
```

Offline executions:

```text
AgentForge.Samples.Console              PASS — 20 + 22 = 42; 2 model calls; 1 tool call
AgentForge.Samples.Streaming            PASS — terminal completion; 1 model call
AgentForge.Samples.MicrosoftExtensionsAI PASS — deterministic IChatClient response
AgentForge.Samples.OpenAI               PASS (guard) — exited without network/provider construction
```

High-risk test coverage includes success, tool round trips and ordering, atomic invalid-batch rejection, model/tool limits, cancellation boundaries, provider/tool failure preservation, malformed responses, authorization and registration, duplicate call IDs, concurrent-run isolation, streaming terminal/disposal behavior, typed-schema constraints, adapter protocol mapping/redaction, endpoint safeguards, retry disablement, keyed DI, and live-gate behavior.

Meaningful gaps: no automated exact-package allow-list/metadata test, no compile test generated directly from README snippets, no output/payload size bound, no proof of cancellation after a remote request is accepted, and no repository artifact that independently establishes the reported post-fix 4/4 live result. No coverage percentage is inferred.

## Clean consumer verification

A temporary project was created outside the repository with `dotnet new console --framework net10.0`. It installed locally generated `AgentForge.Hosting 1.0.0` and `AgentForge.Tools 1.0.0` packages from the temporary package directory. Transitive AgentForge dependencies resolved only from nupkgs; the project had no source project reference. A minimal named agent using `ScriptedAgentModelClient`, `AddAgentForge`, keyed `IAgent` resolution, and a one-call limit compiled and ran, printing:

```text
offline-ok
```

This proves the packed API/dependency graph is consumable, but under the wrong package version and incomplete metadata. Temporary consumer/package artifacts were removed after the audit.

## Remaining limitations

- .NET 10 only; this is intentional.
- In-process trusted tools only; no process sandbox.
- One configured model client per composition; no multi-provider routing.
- No retries, durable execution, memory, persistence, distributed execution, or Phase 3 capabilities.
- Tool execution is sequential.
- Streaming deltas are provisional; only `AgentRunCompleted` is authoritative.
- No framework-enforced token/output/payload-size ceiling beyond model/tool call budgets and caller cancellation.
- MEAI automatic tool-invocation middleware is unsupported.
- Caller-owned provider clients carry caller responsibility for lifetime, endpoint policy, and retry configuration.

## Release blockers and final recommendation

Release blockers:

1. Correct package version and NuGet identity/metadata for all six libraries (AF-REL-001).
2. Prevent sample projects from packing and verify an exact package allow-list (AF-REL-002).
3. Include README, XML documentation, symbols, and SourceLink artifacts (AF-REL-003).
4. Reconcile live-verification evidence and provide complete package installation/quick-start documentation (AF-DOC-001/002).
5. Rerun pack inspection and the clean external consumer against `0.2.0-preview.1`; rerun Release build/tests after these corrections. A new live OpenAI run is required only if reliable post-fix evidence cannot be established through architectural review.

**Final recommendation: NO-GO.** The runtime is a credible preview implementation and the blockers are localized to release engineering and documentation, but publishing the current artifacts would use the wrong version, incomplete legal/provenance metadata, and an unsafe package set. Stop before tagging or publishing and return the packaging/evidence corrections for review.
