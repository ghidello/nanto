# Nanto

> The native frame for your web application.

Nanto is an early-stage .NET framework for building small native applications with web frontends. Its Windows-first design combines a raw Win32 host, the system WebView2 runtime, strongly typed generated C# ↔ TypeScript APIs, capability-based security, and Native AOT by default.

## Status

Phase 1's Windows host and lifecycle-kernel implementation and Phase 2's generated contract and IPC implementation are complete. The versioned bridge provides reflection-free C# registration and dispatch, generated JSON metadata and TypeScript bindings, default-deny capabilities, origin and session checks, cancellation, structured errors, streams, and events across CoreCLR and Native AOT. The Phase 3 implementation candidate adds the CLI, templates, framework-neutral development content, process supervision, telemetry hooks, and optional Aspire composition; its live developer-loop and orchestration evidence remains open in the gate record.

Two Phase 1 acceptance follow-ups remain open without blocking Phase 3: complete the 550-process lifecycle/recovery soak, and pass the visible cross-monitor scenario on two active monitors with different effective DPI. The one-monitor `VisibleDesktop` scenario has passed, and a 45-minute soak attempt completed 397 clean processes before its former safety deadline. See the [Phase 1 gate record](docs/phase1-gate.md) for the exact status and retained evidence.

Window sizes describe the WebView client area in device-independent pixels. Windows chooses the initial screen position in Phase 1, and Nanto uses Per-Monitor-V2 behavior so a user can move the window across displays with different scaling without exposing ambiguous global logical coordinates. Display and work-area changes preserve every partially visible placement; a wholly inaccessible window is moved, without resizing, to the nearest current work area.

Applications may select `System`, `Light`, or `Dark`. Nanto applies that preference to both the shared WebView2 profile and the native Win32 frame, so SPA styles and `matchMedia` receive normal `prefers-color-scheme` updates while the title bar remains consistent. `System` follows the documented `UISettings` color API through a pinned, generated narrow WinRT ABI; Nanto's production assembly has no SDK C#/WinRT projection or `WinRT.Runtime` reference. The untrimmed self-contained CoreCLR Windows runtime pack may still carry its general projection files, while Native AOT removes them from the reachable image. The application—not Nanto—persists a user's choice.

Unexpected main-renderer exits or stalls raise a portable window event and receive one automatic reload attempt. A failed or repeated recovery closes the window through its ordinary lifecycle; browser-process loss also closes normally, while isolated subframe and self-recovering WebView2 child-process failures remain diagnostic events.

Applications may supply an `ILoggerFactory` through `NantoApplicationOptions`. Nanto emits source-generated structured events for application and window lifecycle, the UI thread, WebView2 recovery, versioned assets, and teardown. Nanto-owned fields use opaque storage/bundle/window identifiers and bounded states, operations, codes, counts, and durations; application IDs, titles, routes, local paths, asset names, and frontend data are not logged. The application owns and disposes its logger factory and selects any providers or exporters.

Nanto was renamed from Telaio after the feasibility work; commit [`90725e9`](https://github.com/ghidello/telaio/commit/90725e997fac3140ef4dd9f1a8ebd5d53db67642) records that historical boundary. Nanto has no source, build, report, or undocumented-decision dependency on the former repository. The architecture and Phase 1 plan state every adopted production rule directly.

## Documentation

- [Architecture and roadmap](docs/Nanto-architecture-and-roadmap.md)
- [Phase 1 implementation plan](docs/phase1-plan.md)
- [Phase 1 gate record](docs/phase1-gate.md)
- [Phase 2 implementation plan](docs/phase2-plan.md)
- [Phase 2 gate record](docs/phase2-gate.md)
- [Phase 3 implementation plan](docs/phase3-plan.md)
- [Phase 3 developer-loop baselines](docs/phase3-baselines.md)
- [Phase 3 gate record](docs/phase3-gate.md)
- [Phase 4 implementation plan](docs/phase4-plan.md)
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

Every `*IntegrationTests` assembly receives its integration trait from repository build configuration; individual tests do not need to repeat it. Visible and long-running projects remain separately opted in and are never part of unattended scopes. The current complete scope runs the fast suite, deterministic WebView2 and WinRT-appearance interop regeneration, hidden external-process WebView2 tests, and the self-contained CoreCLR and Native AOT deployment inspection and smoke suites. Visible desktop and multi-monitor acceptance remain a separately approved manual run.

The integration TestApp defaults to `CoreClrFrameworkDependent`: it produces a DLL launched by the installed `dotnet` host. `CoreClrSelfContained` and `NativeAot` are explicit MSBuild modes, independent from `Debug` or `Release`, with isolated `obj/<mode>` and `bin/<mode>` trees. Their dedicated unattended projects publish TestApp in `Release` only when those tests execute, then validate deployment shape, dependency and symbol policy, deterministic ZIP evidence, and the `HostLifecycle`, `Navigation`, and `RendererRecovery` smoke scenarios. Self-contained CoreCLR uses a trusted adjacent `WebView2Loader.dll`; Native AOT uses the pinned `WebView2LoaderStatic.lib` at link time and deploys no loader DLL or CoreCLR runtime. Ignored deployments, separate symbols, ZIPs, and JSON evidence live beneath `artifacts/phase1`; ordinary `dotnet build` creates none of them. The measured TestApp sizes are engineering baselines, include acceptance-only protocol and automation code, and exclude the separately installed Evergreen WebView2 Runtime.

The visible integration tests are self-driving but intentionally interact with the unlocked desktop: they activate and resize their own window, temporarily acquire foreground input when Windows denies a background-launched activation request, change its Nanto appearance preference, send `F6`, and capture BMP screenshots. Run them only by naming the project and supplying both opt-ins:

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests/Nanto.Hosting.Windows.VisibleIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

Successful artifacts are retained beneath `artifacts/phase1/visible/<run-id>` and include the request, report, stdout, stderr, monitor topology, observations, and screenshots. On a machine without two active monitors using different effective DPI, the desktop scenario can pass while the cross-monitor scenario skips and retains `InsufficientDisplays` topology evidence. That mixed-DPI acceptance item remains open even though Phase 1 implementation is closed. Passing `RunManualTests=true` through `Nanto.slnx` is rejected. Automated agents must not run the project as routine verification and must obtain explicit user approval for its desktop effects.

The long-running project is also manual-only, but uses hidden windows and does not interact with the desktop. It runs ten sequential blocks of 50 `HostLifecycle` and 5 `RendererRecovery` processes: 500 lifecycle processes and 50 recovery processes under one application identity and one kill-on-close process group. A measured partial run completed 397 clean processes in 45 minutes, projecting roughly 62–64 minutes for the complete inventory; allow up to the 75-minute safety deadline and approve it separately:

```powershell
dotnet test tests/Nanto.Hosting.Windows.LongRunningIntegrationTests/Nanto.Hosting.Windows.LongRunningIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

Successful child artifacts are removed. While the soak is active, `artifacts/phase1/long-running/<run-id>/progress.json` is atomically updated with the active block, child, scenario, completed count out of 550, and elapsed time; the test also reports each completed block to its console output. A failure retains its request, report when available, stdout, and stderr. The final atomic `summary.json` supersedes the progress file. Passing `RunManualTests=true` through `Nanto.slnx` is rejected, and automated agents must obtain explicit approval before consuming the soak's machine time.

The Milestone 4 implementation keeps WebView2 virtual-host mapping and requires an explicit declared startup asset such as `/index.html`. Client-side history and hash routing work after startup; clean-path reload fallback and service workers are deferred because mapped resources do not raise `WebResourceRequested`, and mapped service-worker scripts are unsupported. The robust future option is a custom response-serving asset host, not a redirect/bootstrap workaround.
