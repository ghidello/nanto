# Nanto

> The native frame for your web application.

Nanto is an early-stage .NET framework for building small native applications with web frontends. Its Windows-first design combines a raw Win32 host, the system WebView2 runtime, strongly typed generated C# ↔ TypeScript APIs, capability-based security, and Native AOT by default.

## Status

Nanto starts with Phase 1: a clean production implementation of the Windows host and lifecycle kernel. Milestones 1–4 established the portable lifecycle, raw-Win32 and WebView2 host, strict embedded asset manifests, content-addressed publication, concurrent shared leases, and exact declared-asset navigation. Milestone 5 has implemented DPI-aware window behavior, native appearance, renderer recovery, and structured diagnostics; mixed-DPI visible acceptance is its remaining gate.

Window sizes describe the WebView client area in device-independent pixels. Windows chooses the initial screen position in Phase 1, and Nanto uses Per-Monitor-V2 behavior so a user can move the window across displays with different scaling without exposing ambiguous global logical coordinates. Display and work-area changes preserve every partially visible placement; a wholly inaccessible window is moved, without resizing, to the nearest current work area.

Applications may select `System`, `Light`, or `Dark`. Nanto applies that preference to both the shared WebView2 profile and the native Win32 frame, so SPA styles and `matchMedia` receive normal `prefers-color-scheme` updates while the title bar remains consistent. `System` follows supported Windows color notifications; the application—not Nanto—persists a user's choice.

Unexpected main-renderer exits or stalls raise a portable window event and receive one automatic reload attempt. A failed or repeated recovery closes the window through its ordinary lifecycle; browser-process loss also closes normally, while isolated subframe and self-recovering WebView2 child-process failures remain diagnostic events.

Applications may supply an `ILoggerFactory` through `NantoApplicationOptions`. Nanto emits source-generated structured events for application and window lifecycle, the UI thread, WebView2 recovery, versioned assets, and teardown. Nanto-owned fields use opaque storage/bundle/window identifiers and bounded states, operations, codes, counts, and durations; application IDs, titles, routes, local paths, asset names, and frontend data are not logged. The application owns and disposes its logger factory and selects any providers or exporters.

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

Every `*IntegrationTests` assembly receives its integration trait from repository build configuration; individual tests do not need to repeat it. Visible and long-running projects remain separately opted in and are never part of unattended scopes. The current complete scope is the cumulative unattended Milestone 5 gate: it runs the fast suite, deterministic interop regeneration, and hidden external-process WebView2 tests. Visible desktop and multi-monitor acceptance remain a separately approved manual run.

The visible integration tests are self-driving but intentionally interact with the unlocked desktop: they activate and resize their own window, temporarily acquire foreground input when Windows denies a background-launched activation request, change its Nanto appearance preference, send `F6`, and capture BMP screenshots. Run them only by naming the project and supplying both opt-ins:

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests/Nanto.Hosting.Windows.VisibleIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

Successful artifacts are retained beneath `artifacts/phase1/visible/<run-id>` and include the request, report, stdout, stderr, monitor topology, observations, and screenshots. On a machine without two active monitors using different effective DPI, the desktop scenario can pass while the cross-monitor scenario skips and retains `InsufficientDisplays` topology evidence; that skip does not complete Milestone 5. Passing `RunManualTests=true` through `Nanto.slnx` is rejected. Automated agents must not run the project as routine verification and must obtain explicit user approval for its desktop effects.

The Milestone 4 implementation keeps WebView2 virtual-host mapping and requires an explicit declared startup asset such as `/index.html`. Client-side history and hash routing work after startup; clean-path reload fallback and service workers are deferred because mapped resources do not raise `WebResourceRequested`, and mapped service-worker scripts are unsupported. The robust future option is a custom response-serving asset host, not a redirect/bootstrap workaround. Native AOT execution remains Milestone 6 work.
