# ADR-001: Toolchain and dependency boundaries

- Status: accepted for M0
- Date: 2026-09-20
- Decision owners: InterCat maintainers

## Context

InterCat needs deterministic offline analysis, a Windows-specific capture edge, and a responsive desktop surface. Capture feasibility is still an M0 question, so platform libraries must not become the domain model or leak into portable analysis code.

## Decision

- Pin the .NET SDK to `10.0.401` with roll-forward disabled.
- Use the latest C# language version supported by that SDK, nullable reference types, recommended analyzers, and warnings as errors solution-wide.
- Pin Avalonia to `11.3.22`. The M0 prototype uses Avalonia custom controls whose drawing path is backed by Avalonia's Skia renderer; direct Skia APIs are deferred until profiling shows a need.
- Pin Microsoft TraceEvent to `3.2.6` and reference it only from `InterCat.Capture.Windows`.
- Keep `InterCat.Domain`, `InterCat.Storage`, and `InterCat.Analysis` independent of Windows and UI assemblies.
- Enforce project-reference direction and prohibited package ownership with an architecture test.

All versions are centrally declared in `Directory.Packages.props`; projects do not carry floating or local package versions.

## Consequences

The solution fails fast when the pinned SDK is missing. Capture adapters can be replaced without changing domain contracts. The first UI remains a prototype rather than evidence that any capture mechanism is supported.

## Upgrade policy and reversal cost

Patch upgrades require a clean build and test run. A .NET LTS, Avalonia major, or ETW adapter change requires an amendment or successor ADR plus replay, interaction, and architecture checks. Replacing TraceEvent is localized to the Windows capture project, but fixture and capability evidence must be regenerated.
