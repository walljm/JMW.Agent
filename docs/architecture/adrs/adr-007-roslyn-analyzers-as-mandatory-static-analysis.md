---
id: adr-007
title: Roslyn analyzers as mandatory static analysis
status: draft
date: 2026-06-04
---
## Context

The architect task card requires linters/static analyzers to be specified with tool names, configuration, and build-pipeline integration, starting from a strict/pedantic ruleset. The project is C# .NET 10.

## Alternatives Considered

1. **No analyzers / IDE-only.** Rejected: analysis must be enforced in the build, not left to editor configuration.
2. **Third-party analyzer packs (StyleCop.Analyzers, SonarAnalyzer.CSharp, Roslynator).** Considered. SonarAnalyzer and Roslynator add value but are additional dependencies (constraints #7); StyleCop is largely style. Adopt the built-in Roslyn analyzers as the mandatory baseline; optional packs may be added later via a dependency record if justified.
3. **Built-in .NET Roslyn analyzers at max level (chosen).**

## Decision

Enable the built-in Roslyn analyzers across all projects via `Directory.Build.props` at the solution root:
- `<AnalysisLevel>latest-Recommended</AnalysisLevel>` (start strict; relaxations documented here),
- `<EnableNETAnalyzers>true</EnableNETAnalyzers>`,
- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`,
- `<Nullable>enable</Nullable>` (enforces the project's no-`!`-suppression and explicit-null rules),
- `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`.

CI runs `dotnet build -warnaserror` and `dotnet format --verify-no-changes`. The `Server.Web` project additionally enables the ASP.NET Core analyzers (`Microsoft.AspNetCore.App` ships API-usage analyzers).

## Rationale

Built-in analyzers are zero-dependency (constraints #7), ship with the SDK, and cover correctness, reliability, and security rules. Warnings-as-errors makes the gate enforceable in CI. `Nullable enable` is the compiler-level backstop for the project's null-handling rule.

## Consequences

- Any rule relaxation must be added to this ADR with justification and scoped via `.editorconfig` severity overrides — not blanket-suppressed.
- Build fails on new warnings; keeps the codebase clean from the start.
- If a third-party analyzer pack is later adopted, it requires a `DEP-NNN` record and an update here.
