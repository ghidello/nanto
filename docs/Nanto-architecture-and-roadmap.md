# Nanto

> **The native frame for your web application.**

## Product description, architecture decisions, and implementation roadmap

**Status:** Phase 1 documentation baseline

**Date:** 10 August 2026

**Target:** Modern .NET, Native-AOT-first, Windows-first applications using system WebViews, with platform-open contracts

---

## 1. Executive summary

Nanto is a .NET framework for building small native applications with a web frontend. It provides the structure that supports an application and connects its web frontend with native .NET capabilities. It follows the useful parts of Tauri's model—a native core process, an operating-system WebView, message-based access to native capabilities, a plugin ecosystem, and capability-based security—but makes C# and modern .NET the application backend.

The feasibility work described as Phase 0 was completed under the former Telaio name. Its source and evidence remain in the [Telaio `phase_1` branch](https://github.com/ghidello/telaio/tree/phase_1) at commit [`0344107`](https://github.com/ghidello/telaio/commit/0344107c06ff36d2f89189bbb7660a46180193b1). Nanto begins with a clean Phase 1 implementation and has no source or build dependency on that repository.

Its defining characteristics are:

- **Developer experience as a defining product feature**, measured from project creation through debugging, telemetry, build, and distribution.
- **Native AOT by default**, with an explicit CoreCLR compatibility mode when an application uses a plugin that cannot support AOT.
- **Operating-system WebViews**, rather than bundling a browser engine.
- **A small platform host**, rather than a general native UI framework.
- **Strongly typed, generated C# ↔ TypeScript APIs**, rather than stringly typed command calls.
- **Compile-time plugin composition**, compatible with trimming and Native AOT.
- **Platform-neutral public APIs** backed by replaceable native implementations.
- **Capability-based security**, treating frontend code as less trusted than native code.
- **Deterministic resource ownership and teardown**, inspired particularly by the lessons in Microsoft.UI.Reactor.
- **A framework-neutral web development experience**: the SPA's own dev server and HMR, .NET Hot Reload where possible, automatic restart where it is not, and optional Aspire orchestration with OpenTelemetry diagnostics. Vite is the reference integration, not a requirement.

The first and only committed production target is Windows. Its substrate will be raw Win32 plus the Evergreen WebView2 Runtime. Nanto will not require WinUI 3, Windows App SDK, XAML, MAUI, WPF, or WinForms. The public model must nevertheless remain independent of Win32 so that future platform experiments remain possible. WKWebView, Android WebView, and WebKitGTK are plausible host technologies, but they are not roadmap commitments.

Nanto is not intended to be a line-by-line port of Tauri or a .NET binding over Tauri. It is a .NET-native interpretation of the same product category.

---

## 2. Product goals

### 2.1 Primary goals

1. Make the complete developer journey unusually good: create, run, edit, inspect, diagnose, test, publish, and understand an application without hidden machinery.
2. Let a .NET developer use any SPA stack that can provide a development URL and produce deployable HTML, JavaScript, CSS, and related assets—including React, Angular, Vue, Svelte, Solid, vanilla TypeScript, and future frameworks Nanto does not yet know about.
3. Produce the smallest practical application by using the system WebView and Native AOT.
4. Make C# APIs feel like ordinary typed TypeScript APIs in the frontend.
5. Provide secure access to operating-system functionality through scoped plugins.
6. Define a coherent host-independent application, window, WebView, and plugin lifecycle without pretending that every platform has identical semantics.
7. Make native resources deterministic, observable, and safe during normal shutdown, failed startup, repeated close requests, and plugin failure.
8. Keep platform frameworks replaceable behind Nanto-owned abstractions.
9. Preserve the normal development experience of the selected SPA toolchain instead of replacing it with a Nanto-specific frontend build system.

### 2.2 Secondary goals

- Make unpackaged and portable Windows applications straightforward.
- Support multiple windows and tray-only applications after the first vertical slice.
- Make first-party plugins consistent in API shape, permissions, diagnostics, and AOT compatibility.
- Make generated APIs useful in JavaScript as well as TypeScript.
- Permit advanced platform-specific plugins without polluting the portable API.
- Integrate cleanly with Aspire and other OTLP-compatible development environments without requiring them in production.

### 2.3 Non-goals

- Nanto is not a native control toolkit.
- Nanto does not attempt to replace React, Angular, Vite, or other frontend tooling.
- Nanto does not bundle Chromium by default.
- Nanto does not provide server-side rendering inside a production desktop application.
- Nanto does not promise identical support for every window feature on every operating system.
- Nanto does not commit to shipping macOS, Linux, Android, or iOS hosts until the Windows product and portable contracts have proved themselves.
- Runtime discovery and loading of arbitrary managed plugin assemblies is not an MVP goal and is incompatible with the default AOT model.
- Nanto will not wrap every operating-system API in core. Most OS integration belongs in plugins.
- Nanto will not depend on Microsoft.UI.Reactor. Reactor is a source of design lessons and test cases.

---

## 3. Positioning

### 3.1 Relationship to Tauri

Tauri uses one core process to manage one or more system WebViews, and keeps sensitive state and native operations in that core. Nanto adopts the same high-level trust boundary and process shape. Tauri's own documentation describes a core process managing WebView processes supplied by the OS and recommends keeping secrets and sensitive business logic out of the frontend ([Tauri process model](https://v2.tauri.app/concept/process-model/)).

Nanto differs in several important ways:

| Concern | Tauri | Nanto |
| --- | --- | --- |
| Backend language | Rust | C# / modern .NET |
| Production compilation | Rust native binary | Native AOT by default; CoreCLR compatibility mode |
| Frontend-to-core calls | Named commands, generally invoked through a generic API | Generated, application-specific TypeScript clients |
| Serialization | Serde | Source-generated `System.Text.Json` |
| Plugin composition | Rust crate plus JavaScript package | Statically referenced NuGet package; TypeScript generated from the same metadata |
| Platform layer | Tauri runtime crates | Small Nanto host per platform |
| Windows host | WebView2 through Tauri's Windows stack | Raw Win32 + direct WebView2 integration |

Nanto should follow Tauri's product discipline, not reproduce its Rust implementation structure.

### 3.2 Relationship to .NET MAUI

MAUI contains valuable ideas:

- a normalized cross-platform window lifecycle;
- platform handlers behind portable abstractions;
- a single-project development model;
- target workloads and platform-native bindings;
- explicit access to platform lifecycle events.

MAUI's lifecycle maps portable events such as created, activated, stopped, resumed, and destroying to native platform events ([.NET MAUI app lifecycle](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/app-lifecycle?view=net-maui-10.0)). Nanto should learn from that normalization.

Nanto should **not** use the MAUI UI framework as its core. It does not need layout, controls, XAML, handlers for an entire widget hierarchy, or the corresponding deployment weight. Platform hosts may use the underlying .NET platform workloads and native bindings where appropriate without depending on `Microsoft.Maui.Controls`.

### 3.3 Relationship to Microsoft.UI.Reactor

Microsoft.UI.Reactor demonstrates several strong Windows engineering patterns:

- retain access to the real native window rather than assuming one abstraction covers every feature;
- establish DPI awareness, COM/WinRT support, an STA UI thread, and a dispatcher before creating windows;
- model desired window state declaratively and apply differences;
- monitor essential Win32 messages even when higher-level window APIs are available;
- centralize DIP-to-pixel conversion;
- enforce UI-thread affinity;
- track windows and other application surfaces explicitly;
- make native close and teardown paths idempotent;
- clean up partially initialized native resources in reverse creation order.

Nanto will borrow these principles and reproduce the relevant behavior using its own host. It will not reference Reactor, WinUI 3, or Windows App SDK.

### 3.4 Relationship to Wails

[Wails](https://wails.io/) is the closest Go equivalent to the product Nanto is trying to become: a compiled Go backend, an arbitrary web frontend, and the operating system's WebView. Wails v2 is the current stable line. Wails v3 is in beta as of August 2026; its desktop API is described as stable, but it is still a pre-GA architecture reference rather than a compatibility target ([Wails v3 beta announcement](https://v3.wails.io/blog/wails-v3-beta/)).

Wails v3 independently validates several Nanto choices:

| Wails v3 choice | Nanto lesson |
| --- | --- |
| Explicit application, window, service, and event objects | Keep ownership visible; do not pass an implicit global framework context through application code. |
| Static analysis generates frontend bindings while retaining names and comments | Roslyn generation should preserve XML documentation, parameter names, nullability, and source locations. |
| Generated JavaScript calls compact numeric method IDs | Consider numeric IDs as a private wire optimization while retaining symbolic names in capabilities, diagnostics, and manifests. |
| Services start in registration order and stop in reverse order | Preserve Nanto's ordered plugin lifecycle and cancellation-first shutdown. |
| A service can carry backend behavior plus frontend assets/scripts | A NuGet plugin can coherently contribute managed code, native implementations, generated TypeScript, permissions, and optional frontend assets. |
| Frontend assets are served through an asset-handler abstraction and can be embedded | Keep `IWebAssetProvider`; support both embedded/single-file and directory-based release profiles. |
| Frontend dev-server HMR; backend rebuild and restart for incompatible changes | Confirms Nanto's split SPA HMR / .NET Hot Reload-or-restart development loop. |
| Build tasks are visible, inspectable, customizable, and have a dry-run mode | Make Nanto's MSBuild/CLI plan observable instead of hiding orchestration in an opaque command. |
| Common and platform-specific event namespaces coexist | Normalize honest common events while retaining typed platform event groups. |
| Missing WebView2 runtime behavior is an explicit build/deployment policy | Model runtime acquisition as policy rather than hard-coded installer behavior. |

Wails v3 also demonstrates the value of automatically cancelling a backend call when its frontend disconnects and of making the calling window available in the invocation context ([Wails bridge](https://v3.wails.io/concepts/bridge/)). Both belong in Nanto's IPC context.

Nanto should not copy everything:

- Wails does not provide Tauri's full capability-manifest model; Nanto keeps its stronger default-deny authorization design.
- Wails' generated numeric method IDs must remain an implementation detail if adopted. Raw numbers must never appear in application code, permission files, or diagnostics without their symbolic command name.
- Wails' public size and memory figures are useful targets, not evidence about Nanto. Nanto measures its own complete artifacts and states whether the shared WebView runtime is included.
- Wails' Go runtime and native wrappers have different ownership constraints. Reactor's explicit teardown discipline remains the stronger model for Nanto's .NET/native boundary.
- Wails v3 is still beta and its documentation currently mixes descriptions of static binding analysis with some runtime service discovery language. Nanto's AOT requirement removes that ambiguity: discovery is compile-time only.

---

## 4. Architecture principles

### 4.1 Nanto owns the public model

No portable Nanto API may expose `HWND`, WebView2, WinUI, UIKit, Android, GTK, or WebKitGTK types. The portable model describes intent; the platform host chooses the native mechanism.

For example:

```csharp
public interface INantoWindow
{
    WindowId Id { get; }
    string Title { get; set; }
    WindowState State { get; }

    ValueTask SetBoundsAsync(WindowBounds bounds, CancellationToken cancellationToken = default);
    ValueTask SetAlwaysOnTopAsync(bool value, CancellationToken cancellationToken = default);
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
```

Platform escape hatches must be explicit and optional:

```csharp
public interface IWindowsWindowHandle
{
    nint Hwnd { get; }
}
```

An application or plugin that consumes this interface knowingly becomes Windows-specific.

### 4.2 Use the lowest durable native substrate

On Windows, Nanto owns the `HWND`; WebView2 and other helpers attach to it. The direction of ownership must never be inverted.

```text
Nanto public API
        ↓
Portable application and window model
        ↓
Nanto.Hosting.Windows
        ↓
HWND + Win32 messages + WebView2
```

If a future Windows API is useful, the Windows host may adopt it behind an adapter. The application remains insulated if Microsoft later replaces that API.

### 4.3 Generate rather than discover

Nanto must avoid runtime assembly scanning, unbounded reflection, dynamic proxy generation, and runtime code emission. Commands, DTO serializers, plugins, permissions, events, and TypeScript clients are discovered at compile time and emitted as code and metadata.

This is both a size decision and an architectural decision. Native AOT trims unused code and cannot support arbitrary runtime code generation; Microsoft recommends source generation and AOT analysis for such applications ([Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)).

### 4.4 Treat the frontend as a separate trust domain

The WebView is not entitled to every capability implemented by the native process. Every command must be registered, authorized, validated, and associated with a known WebView/window and origin.

### 4.5 Every resource has exactly one owner

Native handles, COM interfaces, WebView event subscriptions, cancellation sources, message hooks, plugin scopes, streams, and temporary files must have explicit owners and deterministic release paths. Garbage collection is not a native lifecycle strategy.

### 4.6 Development convenience must not weaken production architecture

Development normally runs on CoreCLR to enable .NET Hot Reload and fast rebuilds. Release normally uses Native AOT. The same generated command registry, serialization contexts, permission checks, and host interfaces must run in both modes so that development does not exercise a different architecture.

### 4.7 Developer experience is part of the architecture

Developer experience is not polish deferred until after the host works. CLI behavior, error messages, generated-code discoverability, hot reload, diagnostics, telemetry, build transparency, and cleanup are public product surfaces with compatibility requirements.

Every feature must answer four questions:

1. How does a developer discover and enable it?
2. What happens during the normal edit/run/debug loop?
3. How is failure explained and repaired?
4. How can the effective configuration and generated behavior be inspected?

Nanto should prefer conventional .NET, npm, Vite, OpenTelemetry, and Aspire concepts over private equivalents. Commands must be scriptable and deterministic as well as pleasant interactively. The CLI must show what it starts, why it restarts, which runtime profile it selected, and how to reproduce a failing step directly.

### 4.8 Frontend integration is a protocol, not a framework dependency

Nanto core must not reference React, Angular, Vue, Svelte, Vite, or a framework-specific router, state library, component model, or build API. Its frontend contract is deliberately small:

- in development, run an optional command and wait for a configured URL to become reachable;
- in production, run an optional build command and consume a configured asset directory;
- emit a framework-neutral ESM JavaScript/TypeScript client into a configurable directory;
- load the resulting URL or assets and communicate through the same generated bridge protocol;
- let the selected frontend tool own transformation, routing, HMR, source maps, and browser debugging.

Templates and adapters may provide excellent defaults for particular ecosystems, but they sit above this contract. A new or uncommon SPA should work through configuration alone; adding it must not require a Nanto runtime release.

---

## 5. High-level system design

```text
┌──────────────────────────────────────────────┐
│ Web frontend                                │
│ React / Angular / Vue / Svelte / vanilla TS │
│ Generated @nanto/app client               │
└──────────────────────┬───────────────────────┘
                       │ typed request/event protocol
┌──────────────────────▼───────────────────────┐
│ Nanto core                                │
│ IPC · authorization · lifecycle · plugins   │
│ generated dispatcher · generated JSON       │
└──────────────────────┬───────────────────────┘
                       │ portable host contracts
┌──────────────────────▼───────────────────────┐
│ Platform host                               │
│ native app · windows · WebView · OS bridge  │
└──────────────────────────────────────────────┘
```

The frontend and core normally live in one application installation, but the WebView renderer may execute in OS-managed child processes. Nanto communicates only through the WebView's supported message bridge; it must not depend on renderer-process internals.

### 5.1 Proposed package layout

| Package | Responsibility |
| --- | --- |
| `Nanto.Core` | Application model, lifecycle, IPC contracts, capabilities, plugin contracts |
| `Nanto.Sdk` | MSBuild integration, build targets, asset collection, publish configuration |
| `Nanto.Generators` | Command dispatcher, JSON context, plugin registry, manifests, TypeScript model |
| `Nanto.Hosting.Windows` | Win32 message loop, HWND windows, WebView2, Windows lifecycle |
| `Nanto.Hosting.Android` | Activity and Android WebView host |
| `Nanto.Hosting.Apple` | Apple lifecycle and WKWebView host, split later if necessary |
| `Nanto.Hosting.Linux` | GTK and WebKitGTK host |
| `Nanto.Cli` | `dotnet nanto new/dev/build/doctor` orchestration |
| `Nanto.Testing` | Protocol harness, fake host, lifecycle and plugin test utilities |
| `Nanto.Plugin.*` | First-party native capabilities |
| `@nanto/core` | Minimal browser-side protocol runtime |
| `@nanto/app` | Generated application-specific TypeScript API |

The exact NuGet names can change before public release. Responsibilities should not.

### 5.2 Platform hosts

| Platform | Native application/window substrate | WebView | Initial status |
| --- | --- | --- | --- |
| Windows | Raw Win32 and `HWND` | Evergreen WebView2 | MVP |
| Android | .NET for Android bindings and `Activity` | Android `WebView` | Later host |
| iOS | .NET platform bind…14283 tokens truncated…ption | Phase 3 developer-experience prototype |
| O-014 | Exact browser telemetry package, default instrumentations, sampling, and relay wire format | Phase 3 telemetry prototype; account for experimental browser instrumentation status |

---

## 14. Implementation roadmap

### Phase 0 — Telaio feasibility and size gates (completed)

**Purpose:** prove the riskiest assumptions before designing broad APIs.

Deliverables:

1. Recover the working Native AOT WebView2 shell from the other computer under its former Albireo name, preserve its source and build inputs, and produce the audit described in section 6.9 before replacing any of its interop work.
2. A Native-AOT Windows executable, based on the recovered spike where appropriate, that:
   - creates an STA Win32 window;
   - uses CsWin32 build-task output with runtime marshalling disabled;
   - refers to Win32 messages, styles, handles, and structures through generated symbolic types;
   - embeds WebView2;
   - loads a minimal framework-neutral static SPA fixture;
   - exchanges request/response JSON messages;
   - resizes correctly;
   - opens developer tools in a development build;
   - survives bounded repeated create/navigate/close cycles with resource-baseline assertions; an optional 1,000-cycle soak remains available for controlled machines.
3. ABI tests for generated Win32 and WebView2 declarations on x64 and Arm64, including callback calling conventions, GUIDs, interface inheritance/vtable shape, string marshalling, and structure sizes.
4. A documented WebView2 COM interop comparison covering standalone `Microsoft.Web.WebView2.Core.Projection`, a narrow source-generated COM projection from official IDL/header, CsWin32/WebView2 metadata where viable, and manual ABI code only for remaining gaps. The selected path has no unexplained AOT/trim warnings and no unwanted UI-framework dependencies.
5. Loader comparison: architecture-specific `WebView2Loader.dll` baseline versus Native AOT static linking if technically viable, including complete output size and startup behavior.
6. Asset-serving proof covering virtual-host mapping from a directory and a versioned extracted embedded bundle, plus optional request interception for dynamic content.
7. STA/threading tests that fail on cross-thread WebView2 access and prove that no synchronous wait blocks WebView2 completion callbacks.
8. Failure/recovery tests for missing runtime, unwritable or locked UDF, renderer failure, browser-process failure, runtime update, and environment reconfiguration.
9. Baseline measurements:
   - executable and application-directory size;
   - compressed distribution size;
   - cold/warm startup time;
   - idle working set;
   - window creation and teardown time;
   - leaked handles/COM references after stress runs.
10. CoreCLR comparison build using identical host contracts.

Exit criteria:

- Native AOT WebView2 messaging and deterministic teardown work reliably.
- The recovered Albireo spike's working mechanism and dependencies are understood, reproduced, and either adopted deliberately or rejected with recorded reasons.
- No raw OS magic numbers or hand-copied Win32 signatures remain in ordinary host code.
- CsWin32 and Win32 metadata versions are pinned and their generated surface is recorded for review.
- No dependency on WinUI, Windows App SDK, WPF, WinForms, or MAUI UI appears in the output.
- The selected WebView2 projection works with `DisableRuntimeMarshalling=true` and produces no unexplained IL2026, IL3050, IL3052, or equivalent AOT/trim warnings.
- Every WebView2 subscription is removed before release; the controller closes before its parent `HWND` is destroyed; environment/UDF operations wait for `BrowserProcessExited` when required.
- Production assets load from a secure virtual HTTPS origin without a loopback server, and ordinary static requests do not cross the managed UI thread.
- The team understands every AOT warning and native dependency.
- The measured result supports continuing the small-host approach.

### Phase 1 — Windows host and lifecycle kernel

Deliverables:

- `Nanto.Core` lifecycle and host contracts;
- `Nanto.Hosting.Windows` application host, UI dispatcher, message pump, window registry, and WebView host;
- curated CsWin32 API/constant inputs and a generated-interop review test;
- explicit application/window state machines;
- reverse-order cleanup stack for partial initialization;
- DPI-correct sizing and multi-monitor behavior;
- navigation policy and production asset provider;
- structured logging and resource ledger;
- fake host for lifecycle unit tests.

Exit criteria:

- repeated and concurrent close paths are idempotent;
- injected failure at every initialization step releases prior resources;
- UI-thread violations fail clearly in development;
- renderer failure produces a controlled lifecycle event rather than a process crash.

### Phase 2 — IPC and generated contracts

Deliverables:

- versioned request/response protocol;
- cancellation, structured errors, typed events, and streams;
- `[NantoApi]`/`[NantoCommand]` prototype;
- incremental generator for dispatcher and JSON context;
- TypeScript model and `@nanto/app` output;
- preserved XML documentation, parameter names, nullability, and source locations in generated frontend APIs;
- compact numeric command-ID experiment with collision checks and a symbolic diagnostic manifest;
- diagnostics for unsupported signatures;
- protocol and generator golden tests.

Exit criteria:

- changing a C# DTO produces a deterministic TypeScript change;
- invalid contracts fail at compile time;
- no runtime command or DTO discovery is used;
- all generated paths pass strict AOT publish;
- cancellation and stream shutdown are race-tested.

### Phase 3 — CLI, framework-neutral SPA integration, and development loop

Deliverables:

- `dotnet new nanto --frontend react`;
- `--frontend custom` or equivalent attachment flow for an arbitrary existing SPA;
- `dotnet nanto dev`, `build`, and `doctor`;
- `dotnet nanto build --plan` or equivalent dry-run/target inspection;
- generic frontend-command startup, URL readiness detection, and lifecycle management;
- Vite reference integration and at least one materially different SPA toolchain conformance test;
- frontend-owned HMR/live reload without coupling the host to a bundler API;
- C# Hot Reload where supported and clear restart behavior otherwise;
- regenerated TypeScript triggering frontend type checking/HMR;
- unified structured console output;
- standard host `ActivitySource`, `Meter`, and logging instrumentation;
- W3C Trace Context propagation across generated bridge calls;
- optional Aspire sample/AppHost orchestration for the Nanto host, a frontend resource, and at least one dependent service;
- end-to-end frontend → native host → dependent service trace in the Aspire dashboard;
- clean child-process termination on Ctrl+C and failure.

Exit criteria:

- a new developer can create and run the React reference application using the documented commands;
- an existing non-React SPA can be attached using configuration without changing or extending the Nanto runtime;
- frontend edits update without restarting the native host;
- common C# edits update or restart predictably;
- the effective build graph and external commands can be inspected without executing them;
- the ordinary development loop requires neither Aspire nor an OTLP collector;
- opting into Aspire shows resource health, logs, traces, and metrics without changing application command code;
- telemetry can be disabled and trimmed without changing runtime behavior;
- no orphaned Node or .NET processes remain after exit;
- spaces in paths, multiple package managers, and occupied dev ports are tested.

### Phase 4 — Capabilities and plugin foundation

Deliverables:

- capability schema, compiler, and generated authorization table;
- per-window and per-origin grants;
- scoped permission validation;
- static plugin registry;
- application/window plugin scopes;
- reverse-order plugin start/stop;
- AOT compatibility metadata and verification;
- runtime-selection diagnostics.

Exit criteria:

- ungranted commands cannot be invoked even when implemented;
- remote navigation loses local capabilities by default;
- plugin startup failure cleans up already-started plugins;
- strict AOT mode identifies the exact incompatible dependency;
- CoreCLR fallback is explicit in build output and artifacts.

### Phase 5 — Windows MVP plugins and distribution

Deliverables:

- clipboard;
- dialogs;
- filesystem with scoped paths;
- opener;
- logging;
- OS/application information;
- single-instance activation;
- unpackaged release output;
- size report and release symbols;
- signing/installer exploration, without making it core.

Exit criteria:

- each plugin has permissions, typed bindings, lifecycle tests, AOT tests, and documentation;
- a representative sample application builds in AOT and CoreCLR modes;
- missing WebView2 Runtime produces an actionable recovery path;
- production defaults disable development-only features.

### Phase 6 — Multi-window, tray, and shell integration

Deliverables:

- multiple windows and ownership relationships;
- shutdown policies and peer surface registry;
- menu and tray plugin;
- notifications and global shortcuts;
- taskbar integration where useful;
- persisted window placement with DPI-safe restoration.

Exit criteria:

- tray-only applications shut down only according to policy;
- auxiliary windows cannot accidentally terminate the application;
- all shell resources are removed after abnormal and normal close paths.

### Phase 7 — Potential additional platform hosts (uncommitted)

This phase is an architectural option, not a product commitment. It begins only after the Windows product is successful and there is concrete demand, sustainable maintenance capacity, and a credible Native AOT/WebView/toolchain story for one additional platform.

Implement one host at a time, preserving the same conformance suite:

1. Choose the next platform based on user demand and AOT feasibility.
2. Implement native lifecycle, window, WebView transport, assets, and dispatcher.
3. Run core IPC/security/plugin conformance tests unchanged.
4. Add platform-specific lifecycle, resource, packaging, and store tests.
5. Port only plugins that can provide honest semantics.

Whether any platform follows Windows should be decided after Phase 5. Android and Apple platforms introduce lifecycle/AOT constraints distinct from desktop; Linux introduces distribution variability. Keeping Windows types out of core and maintaining a host conformance suite preserves the option. It does not oblige Nanto to implement every host or force the Windows design toward a lowest-common-denominator API.

---

## 15. Testing strategy

### 15.1 Required test layers

| Layer | Tests |
| --- | --- |
| Core | Lifecycle state machines, authorization, protocol, cancellation, shutdown policy |
| Generators | Roslyn snapshot/golden tests, diagnostics, deterministic output, incremental behavior |
| TypeScript | Type tests, serialization fixtures, AbortSignal and AsyncIterable behavior |
| Windows unit | DIP conversion, message decoding, ownership and cleanup-stack behavior |
| Windows integration | Real HWND/WebView2, focus, resize, navigation, renderer failure, close races |
| AOT | Strict publish and smoke tests for every sample/plugin combination |
| Security | Origin spoofing, malformed messages, unauthorized commands, scope escapes, navigation |
| Dev loop | Generic dev-server readiness, HMR/live reload, generated-binding update, host restart, process cleanup |
| Telemetry | W3C context propagation, frontend/native span parenting, redaction, sampling, batching, relay limits, shutdown flush, disabled-listener overhead |
| Aspire integration | Resource readiness/order, frontend and host endpoints, dependent-service references, dashboard OTLP, Ctrl+C cleanup |
| Packaging | Clean VM/machine, WebView runtime present/missing, signed/unsigned paths |

### 15.2 Lifecycle fault testing

Every native acquisition point should be numbered. Tests inject failure after each point and assert that the resource ledger returns to baseline. Stress tests should include:

- close during WebView initialization;
- two simultaneous close requests;
- application shutdown while a command is streaming;
- plugin stop throwing an exception;
- renderer process failure;
- navigation during shutdown;
- repeated open/close across different DPIs;
- Ctrl+C during every dev-orchestration stage.

### 15.3 Compatibility matrix

CI should cover:

- supported Windows versions;
- Windows x64 initially; future architectures receive their own compatibility gates before support is promised;
- WebView2 Evergreen versions within the supported policy;
- Native AOT and CoreCLR profiles;
- debug/development and production security settings;
- the React/Vite reference plus at least one non-React or non-Vite SPA toolchain;
- clean machines with and without preinstalled prerequisites.

---

## 16. Quality gates and engineering policies

1. **AOT gate:** strict AOT builds contain no unexplained AOT or trim warnings.
2. **Lifecycle gate:** every native resource has a documented owner and teardown path.
3. **Failure gate:** every partial initialization path is fault-injection tested.
4. **Security gate:** new commands and plugins are inaccessible until granted.
5. **API gate:** no portable public API leaks platform framework types.
6. **Size gate:** each release records compressed, installed, and executable size deltas.
7. **Protocol gate:** generated C# and TypeScript fixtures agree byte-for-byte where required.
8. **Dev-loop gate:** CLI exit leaves no child processes or locked files.
9. **Interop gate:** native/COM errors are translated at narrow boundaries with useful diagnostics.
10. **Documentation gate:** platform differences and unsupported semantics are stated explicitly.
11. **Developer-experience gate:** project creation, first run, incremental feedback, restart explanations, diagnostics, and recovery paths meet recorded budgets and usability tests.
12. **Telemetry gate:** instrumentation is standard, correlated, redacted, bounded, optional, and does not compromise Native AOT or the no-listener size/performance budget.

---

## 17. Immediate next actions

Phase 0 has passed and its implementation is evidence, not a production dependency. Continue with [`phase1-plan.md`](phase1-plan.md) in vertical milestone order:

1. Create the canonical production-only Nanto solution while keeping Phase 0 source and tests in Telaio.
2. Create the portable Core, Windows host, Testing, and fast-test projects without adding UI frameworks or broad hosting infrastructure.
3. Implement and exhaustively test the portable lifecycle state machines, cancellation-first shutdown, cleanup aggregation, application identity, and immutable registry snapshots.
4. Add the dedicated STA dispatcher and raw Win32 x64 host, numbering and fault-injecting every native acquisition as it is introduced.
5. Bring the proven generated WebView2 COM projection, static-loader AOT path, secure origin, embedded versioned cache, routing policy, and teardown ownership into production code through hidden integration tests.
6. Keep visible-window, Native AOT, and long-running tests in explicit projects outside the root solution; do not add Arm64, macOS, Linux, templates, plugins, packaging, or multi-window scope during Phase 1.

---

## 18. Success criteria for the Nanto MVP

The Windows MVP is successful when a developer can use the React/Vite reference path:

```bash
dotnet new nanto -n Sample --frontend react
cd Sample
dotnet nanto dev
dotnet nanto build --runtime native-aot
```

and obtain an application that:

- starts a small raw Win32 host with WebView2;
- preserves the selected frontend development server's normal HMR/live-reload behavior;
- reports clearly when a managed or native change requires host restart;
- exposes generated, typed C# APIs to TypeScript;
- exposes the same framework-neutral ESM bridge client to an arbitrary configured SPA;
- enforces declared capabilities;
- uses a small set of typed first-party plugins;
- runs without a separately installed .NET runtime in AOT mode;
- does not bundle a browser runtime in its normal distribution;
- shuts down cleanly with no leaked native resources;
- can opt into CoreCLR when a required plugin cannot support AOT;
- runs its normal development loop without Aspire, but can opt into Aspire orchestration and see correlated frontend, native, and dependent-service telemetry;
- does not expose Win32 details to portable application code.

That vertical slice is the proof that Nanto is more than “WebView2 hosted from C#”: it is a coherent, secure, typed, AOT-oriented application framework.

---

## 19. Primary references

- [Tauri architecture](https://v2.tauri.app/concept/architecture/)
- [Tauri process model](https://v2.tauri.app/concept/process-model/)
- [Tauri commands and events](https://v2.tauri.app/develop/calling-rust/)
- [Tauri development workflow](https://v2.tauri.app/develop/)
- [Tauri configuration](https://v2.tauri.app/reference/config/)
- [Tauri security](https://v2.tauri.app/security/)
- [Microsoft.UI.Reactor repository](https://github.com/microsoft/microsoft-ui-reactor)
- [Reactor Windows guide](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/guide/windows.md)
- [Reactor window design](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/specs/036-window-design.md)
- [Microsoft CsWin32](https://github.com/microsoft/CsWin32)
- [CsWin32 getting started and Native AOT configuration](https://microsoft.github.io/CsWin32/docs/getting-started.html)
- [Microsoft Win32 metadata](https://github.com/microsoft/win32metadata)
- [Microsoft guidance for calling Win32 APIs from C#](https://learn.microsoft.com/en-us/windows/apps/develop/interop/call-win32-apis)
- [Current CsWin32/WebView2 metadata issue](https://github.com/microsoft/CsWin32/issues/1695)
- [WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- [Historical Telaio WebView2 host binding comparison](https://github.com/ghidello/telaio/blob/0344107c06ff36d2f89189bbb7660a46180193b1/docs/research/webview2-host-bindings.md)
- [.NET Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [.NET trimming guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming)
- [.NET MAUI lifecycle](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/app-lifecycle?view=net-maui-10.0)
- [Android WebView](https://developer.android.com/develop/ui/views/layout/webapps/webview)
- [Apple WKWebView](https://developer.apple.com/documentation/webkit/wkwebview)
- [WebKitGTK](https://webkitgtk.org/)
- [Wails v3 beta architecture announcement](https://v3.wails.io/blog/wails-v3-beta/)
- [Wails v3 architecture](https://v3.wails.io/concepts/architecture/)
- [Wails v3 application lifecycle](https://v3.wails.io/concepts/lifecycle/)
- [Wails v3 bridge](https://v3.wails.io/concepts/bridge/)
- [Wails v3 build system](https://v3.wails.io/concepts/build-system/)
- [Aspire executable resources](https://learn.microsoft.com/en-us/dotnet/aspire/app-host/executable-resources)
- [Aspire JavaScript and Vite integration](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/build-aspire-apps-with-nodejs)
- [Aspire telemetry](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/telemetry)
- [Aspire browser telemetry](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/enable-browser-telemetry)
- [Standalone Aspire dashboard](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/standalone)
- [OpenTelemetry JavaScript](https://opentelemetry.io/docs/languages/js/)
- [OpenTelemetry browser instrumentation](https://opentelemetry.io/docs/languages/js/getting-started/browser/)
- [OpenTelemetry browser exporters](https://opentelemetry.io/docs/languages/js/exporters/)
