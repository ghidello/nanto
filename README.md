# Nanto

> The native frame for your web application.

Nanto is an early-stage .NET framework for building small native applications with web frontends. Its Windows-first design combines a raw Win32 host, the system WebView2 runtime, strongly typed generated C# ↔ TypeScript APIs, capability-based security, and Native AOT by default.

## Status

Nanto starts with Phase 1: a clean production implementation of the Windows host and lifecycle kernel. Milestones 1–4 established the portable lifecycle, raw-Win32 and WebView2 host, strict embedded asset manifests, content-addressed publication, concurrent shared leases, and exact declared-asset navigation. Milestone 5 is adding DPI-aware window behavior, native appearance, renderer recovery, and diagnostics in separately reviewed batches.

Window sizes describe the WebView client area in device-independent pixels. Windows chooses the initial screen position in Phase 1, and Nanto uses Per-Monitor-V2 behavior so a user can move the window across displays with different scaling without exposing ambiguous global logical coordinates. Display and work-area changes preserve every partially visible placement; a wholly inaccessible window is moved, without resizing, to the nearest current work area.

Applications may select `System`, `Light`, or `Dark`. Nanto applies that preference to the shared WebView2 profile, so SPA styles and `matchMedia` receive normal `prefers-color-scheme` updates. The application—not Nanto—persists a user's choice.

Nanto was renamed from Telaio after the feasibility work; commit [`90725e9`](https://github.com/ghidello/telaio/commit/90725e997fac3140ef4dd9f1a8ebd5d53db67642) records that historical boundary. Nanto has no source, build, report, or undocumented-decision dependency on the former repository. The architecture and Phase 1 plan state every adopted production rule directly.

## Documentation

- [Architecture and roadmap](docs/Nanto-architecture-and-roadmap.md)
- [Phase 1 implementation plan](docs/phase1-plan.md)
- [Contributor and coding-agent guidance](AGENTS.md)

## Requirements

The planned implementation targets Windows 10 22H2/build 19045 or newer on x64 and uses the .NET 10 SDK selected by [`global.json`](global.json).

## Development

The canonical default checks are:

```powershell
dotnet build
dotnet test
```

Test inventory is selected independently from the `Debug` or `Release` build configuration. The canonical commands are:

```powershell
dotnet test                              # fast tests only
dotnet test -p:TestScope=All             # fast and unattended integration tests
dotnet test -p:TestScope=Integration     # unattended integration tests only
```

Every `*IntegrationTests` assembly receives its integration trait from repository build configuration; individual tests do not need to repeat it. Visible and long-running projects remain separately opted in and are never part of unattended scopes. The current complete scope is the cumulative Milestone 5 gate: it runs the fast suite, deterministic interop regeneration, and hidden external-process WebView2 tests. Visible multi-monitor acceptance remains a separately approved manual run.

Once introduced, manual integration tests must be run by naming exactly one project and supplying both opt-ins:

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests/Nanto.Hosting.Windows.VisibleIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
dotnet test tests/Nanto.Hosting.Windows.LongRunningIntegrationTests/Nanto.Hosting.Windows.LongRunningIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

Visible tests interact with the desktop; long-running tests may occupy the machine for substantial time. Passing `RunManualTests=true` through `Nanto.slnx` is rejected so the two categories cannot start together accidentally. Automated agents must not run either project as routine verification. When a manual test is needed to validate relevant behavior, the agent must explain why and obtain explicit user approval before running that specific project.

The Milestone 4 implementation keeps WebView2 virtual-host mapping and requires an explicit declared startup asset such as `/index.html`. Client-side history and hash routing work after startup; clean-path reload fallback and service workers are deferred because mapped resources do not raise `WebResourceRequested`, and mapped service-worker scripts are unsupported. The robust future option is a custom response-serving asset host, not a redirect/bootstrap workaround. Native AOT execution remains Milestone 6 work.
