# AgentForge.NET v0.2.0-preview.1 Release-Blocker Remediation

Remediation date: 2026-10-18 (Asia/Calcutta)

Original audited commit: `f326b9030d1da5cd32e43d15910d72831577399d`

Recommendation: **GO for the authorized first GitHub push. Do not publish NuGet packages until separately authorized.**

## Summary

All five confirmed release-blocker findings are resolved. The six library packages use version `0.2.0-preview.1`, complete MIT and repository metadata, README and XML documentation, deterministic GitHub SourceLink, and matching symbol packages. Samples, tests, and live-verification executables are non-packable. Documentation contains complete installation and usage guidance and accurately labels the supplied OpenAI result as user-reported evidence.

No runtime public API was changed. No paid provider request, tag, GitHub release, or NuGet publication was made.

## Finding resolutions

### AF-REL-001 — Resolved

- All six package IDs are preserved at `0.2.0-preview.1`.
- Internal `AgentForge.*` dependencies resolve to `0.2.0-preview.1`.
- NuGet metadata uses author `Subhajit Labs`, product `AgentForge.NET`, copyright `Copyright (c) 2026 Subhajit Labs`, MIT license expression, repository type `git`, and repository URL `https://github.com/subhajitlabs/AgentForge.NET`.
- Package-specific descriptions and README metadata are present.
- The repository contains the standard MIT `LICENSE` text with the authorized copyright.

### AF-REL-002 — Resolved

- All samples, tests, and live-verification executables are non-packable.
- Solution packing creates exactly six `.nupkg` files and six corresponding `.snupkg` files.
- `eng/Verify-Packages.ps1` enforces the exact allow-list, version, authorship, copyright, license, repository metadata, descriptions, README/XML content, symbols, and internal dependency versions.

### AF-REL-003 — Resolved

- All libraries generate and pack XML documentation.
- All libraries produce portable-PDB `.snupkg` files.
- `Microsoft.SourceLink.GitHub` is centrally pinned to `10.0.402` and privately referenced by each publishable project.
- All six PDBs contain deterministic GitHub raw-source mappings for the canonical repository and source revision.
- README content is included in each primary package.

### AF-DOC-001 — Resolved

`README.md` and `docs/CURRENT_STATUS.md` record the supplied post-fix `gpt-4o-mini` outcomes as user-reported evidence without inventing a timestamp or exact live-run commit:

- Buffered: PASS, 1 model call, 0 tool calls.
- Streaming: PASS, 1 model call, 0 tool calls.
- Typed tool round trip: PASS, 2 model calls, 1 tool call.
- Caller cancellation: PASS, 0 completed model calls, 0 tool calls.

The pre-cancelled-token limitation remains explicit: this proves local propagation and no successful completion, not cancellation of remote work already accepted by OpenAI.

### AF-DOC-002 — Resolved

The README contains versioned package installation commands and combinations, a complete offline named-agent quick start, typed-tool and streaming examples, OpenAI configuration, raw Microsoft.Extensions.AI guidance, supported features, and unsupported scope. The complete quick start compiled and ran from local packages without project references.

## Files changed

- `Directory.Build.props`
- `Directory.Packages.props`
- `LICENSE`
- `README.md`
- `docs/CURRENT_STATUS.md`
- `docs/PHASE2_PROPOSAL.md`
- `docs/PHASE2_RELEASE_AUDIT.md`
- `docs/PHASE2_RELEASE_REMEDIATION.md`
- `eng/Verify-Packages.ps1`
- All six production `.csproj` files
- All four sample `.csproj` files
- The audited typed-tool/OpenAI compatibility correction and regression tests recorded by `docs/PHASE2_RELEASE_AUDIT.md`

The changes were reviewed together because the audit explicitly identified the pre-existing working-tree corrections as part of the release source state.

## Package metadata and artifact inspection

| Package ID | Version | README | XML docs | Symbols | Internal AgentForge dependencies |
|---|---|---:|---:|---:|---|
| AgentForge.Abstractions | 0.2.0-preview.1 | Yes | Yes | Yes | None |
| AgentForge.Core | 0.2.0-preview.1 | Yes | Yes | Yes | Abstractions 0.2.0-preview.1 |
| AgentForge.Hosting | 0.2.0-preview.1 | Yes | Yes | Yes | Abstractions/Core 0.2.0-preview.1 |
| AgentForge.Tools | 0.2.0-preview.1 | Yes | Yes | Yes | Abstractions 0.2.0-preview.1 |
| AgentForge.Providers.OpenAI | 0.2.0-preview.1 | Yes | Yes | Yes | Abstractions 0.2.0-preview.1 |
| AgentForge.Adapters.MicrosoftExtensionsAI | 0.2.0-preview.1 | Yes | Yes | Yes | Abstractions 0.2.0-preview.1 |

Every nuspec contains the expected MIT, copyright, author, repository, README, description, and dependency metadata. No sample or test package was produced.

## SourceLink and symbols verification

Six `.snupkg` files were created, each containing its portable PDB. `sourcelink` 3.1.1 `print-json` returned a mapping for every PDB in this form:

```json
{"documents":{"/_/*":"https://raw.githubusercontent.com/subhajitlabs/AgentForge.NET/<source-revision>/*"}}
```

The package build after the release commit must be used for final remote-download validation so `<source-revision>` equals the pushed commit.

## Verification results

```text
dotnet restore AgentForge.slnx
  PASS — all projects restored.

dotnet build AgentForge.slnx -c Release --no-restore
  PASS — 0 warnings, 0 errors.

dotnet test AgentForge.slnx -c Release --no-build
  PASS — 203/203 tests:
  Core 104; Tools 31; OpenAI 33; MEAI 23; live guards 12.

dotnet pack AgentForge.slnx -c Release --no-build -o ./artifacts/packages
  PASS — 0 warnings; exactly six nupkg and six snupkg artifacts.

eng/Verify-Packages.ps1 -PackageDirectory ./artifacts/packages
  PASS — package allow-list, identities, metadata, contents, symbols, and dependencies verified.

git diff --check
  PASS — no whitespace errors; Git emitted line-ending conversion notices only.
```

## Clean consumer verification

A clean external .NET 10 console project installed `AgentForge.Hosting` version `0.2.0-preview.1` from the local package directory without project references. The README quick-start source compiled and ran successfully, printing:

```text
Reset instructions are available on the account page.
```

## Repository and hygiene verification

- The current branch was renamed in place from `master` to `main`; history was not rewritten.
- `origin` is `https://github.com/subhajitlabs/AgentForge.NET.git` for fetch and push.
- `git ls-remote --heads --tags origin` returned no refs with exit code 0 before the first push.
- Changed and untracked files were reviewed. Generated packages and temporary consumer/tool directories are excluded from the commit and removed after verification.
- Secret scanning found only intentional synthetic test credentials and environment-variable names; no real credential, private key, connection string, generated binary, or IDE file is approved for staging.

## Remaining limitations

The non-blocking product limitations recorded in the audit remain: .NET 10 only, trusted in-process and sequential tools, no framework-enforced output/payload ceiling, no retries/durability/persistence/memory/routing, and no proof of cancellation after OpenAI accepts remote work. These are documented preview constraints, not release blockers.

## Final recommendation

**GO for the authorized first GitHub push.** All release-readiness blockers are resolved and offline checks pass. NuGet publication, tagging, and GitHub release creation remain explicitly out of scope and require separate authorization.
