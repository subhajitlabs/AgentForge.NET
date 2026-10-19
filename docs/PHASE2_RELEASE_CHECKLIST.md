# AgentForge.NET v0.2.0-preview.1 Release Checklist

This checklist separates validation from the founder-authorized publication stages. Gate A authorizes only the first section. Do not tag, create a GitHub release, publish to NuGet, or make a paid provider call without separate authorization.

## 1. Pre-publication validation (Gate A)

- [x] Confirm the source baseline and review the release audit and remediation.
- [x] Restore every solution project from committed lock files with `dotnet restore AgentForge.slnx --locked-mode`.
- [x] Build Release with zero warnings and errors.
- [x] Run all deterministic offline tests with live gates disabled and credential variables empty.
- [x] Pack the solution and produce exactly six `0.2.0-preview.1` library packages and six matching symbol packages.
- [x] Run `eng/Verify-Packages.ps1` against the package directory.
- [x] Verify exact IDs and versions, internal dependency versions, MIT license, repository metadata, README files, XML documentation, and portable PDB presence.
- [x] Verify the package repository commit against the source commit. On GitHub Actions this is `GITHUB_SHA`.
- [x] Confirm sample, test, and live-verification projects produce no packages.
- [x] Record that the OpenAI result is user-reported and was not rerun during Gate A.
- [x] Confirm no package has been published and no public API baseline has yet been established.
- [ ] Obtain founder review of the Gate A diff and a successful GitHub-hosted CI run. A local workflow inspection is not a hosted run.

## 2. Git tag and GitHub release (requires explicit authorization)

- [ ] Confirm the reviewed commit on `main` is the commit that passed hosted CI.
- [ ] Rebuild packages from that exact clean commit and verify their repository revision.
- [ ] Create the approved version tag for `v0.2.0-preview.1` and push it.
- [ ] Create a GitHub prerelease from that tag with release notes and the verified `.nupkg`/`.snupkg` artifacts.
- [ ] Confirm the tag, release commit, and artifact source revision are identical.

## 3. NuGet publication (requires explicit authorization)

- [ ] Use the artifacts rebuilt from the tagged commit; do not repack from a different tree.
- [ ] Publish only the six allow-listed `.nupkg` files. Do not publish samples, tests, or `.snupkg` files as primary packages.
- [ ] Confirm NuGet accepted each matching symbol package through the selected publication path.
- [ ] Record publication output without recording an API key or other credential.

## 4. Public indexing and download verification

- [ ] Wait for all six versions to become searchable and downloadable from the public NuGet feed.
- [ ] Download every `.nupkg` and `.snupkg` from the public feed and rerun package verification.
- [ ] Confirm package pages show the correct license, repository, README, version, dependencies, and symbols.
- [ ] Confirm SourceLink resolves source at the tagged commit.

## 5. Clean external consumer verification

- [ ] In a new directory outside this repository, create a .NET 10 console project with no project references or repository-local package source.
- [ ] Install `AgentForge.Hosting` and `AgentForge.Tools` version `0.2.0-preview.1` from nuget.org.
- [ ] Build and run the README offline quick start.
- [ ] Repeat restore/build using the OpenAI and Microsoft.Extensions.AI adapter packages without making a live provider call.
- [ ] Record SDK version, commands, output, and resolved dependency graph.

## Public API compatibility baseline

The six packages have public compatibility surfaces, but no earlier published AgentForge package or approved API-baseline file exists in this repository. Automated compatibility enforcement is therefore feasible only after this release is frozen: capture the reviewed `0.2.0-preview.1` assemblies or generated API text as the initial baseline, commit it in a separately reviewed change, and compare later builds against it. Gate A must not invent a comparison result or add a new dependency solely to compare the release with itself.
