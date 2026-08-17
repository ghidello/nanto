# Nanto

> **The native frame for your web application.**

## Product description, architecture decisions, and implementation roadmap

**Status:** Phase 1 implementation complete; Phase 2 is current. Two Phase 1 acceptance follow-ups remain recorded in [`phase1-gate.md`](phase1-gate.md).

**Date:** 10 August 2026

**Target:** Modern .NET, Native-AOT-first, Windows-first applications using system WebViews, with platform-open contracts

---

## 1. Executive summary

Nanto is a .NET framework for building small native applications with a web frontend. It provides the structure that supports an application and connects its web frontend with native .NET capabilities. It follows the useful parts of Tauri's model—a native core process, an operating-system WebView, message-based access to native capabilities, a plugin ecosystem, and capability-based security—but makes C# and modern .NET the application backend.

Nanto was renamed from Telaio after feasibility work was completed; commit [`90725e9`](https://github.com/ghidello/telaio/commit/90725e997fac3140ef4dd9f1a8ebd5d53db67642) records that historical boundary. Nanto begins with a clean Phase 1 implementation and has no source, build, report, or undocumented-decision dependency on the former repository. Every adopted production rule is stated here or in the Phase 1 plan.

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
    string Title { get; }
    WindowSize Size { get; }
    WindowState State { get; }

    ValueTask SetTitleAsync(string title, CancellationToken cancellationToken = default);
    ValueTask SetSizeAsync(WindowSize size, CancellationToken cancellationToken = default);
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
```

Platform escape hatches must be explicit and optional. The following is a possible future Windows-specific contract, not a Phase 1 API; F-002 defers it until a concrete consumer exists:

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
| `Nanto.Core` | Application model, public host-authoring contracts, lifecycle, IPC contracts, capabilities, plugin contracts |
| `Nanto.Sdk` | MSBuild integration, build targets, asset collection, publish configuration |
| `Nanto.Generators` | Command dispatcher, JSON context, plugin registry, manifests, TypeScript model |
| `Nanto.Hosting.Windows` | Win32 message loop, HWND windows, WebView2, Windows lifecycle |
| `Nanto.Hosting.Android` | Activity and Android WebView host |
| `Nanto.Hosting.Apple` | Apple lifecycle and WKWebView host, split later if necessary |
| `Nanto.Hosting.Linux` | GTK and WebKitGTK host |
| `Nanto.Cli` | `dotnet nanto new/dev/build/doctor` orchestration |
| `Nanto.Testing` | Future public testing package; Phase 1 keeps fake host, dispatcher, recorder, and failure scripting in non-packable repository support |
| `Nanto.Plugin.*` | First-party native capabilities |
| `@nanto/core` | Minimal browser-side protocol runtime |
| `@nanto/app` | Generated application-specific TypeScript API |

The exact NuGet names can change before public release. Responsibilities should not.

`Nanto.Core` exposes two deliberate portable API layers. The `Nanto` namespace is application-facing. The `Nanto.Hosting` namespace is the supported host-authoring surface for platform implementations: canonical application identity and option validation, application and window lifecycle state machines, asset-preparation context creation, and reverse-order asynchronous cleanup. Production hosts consume this public surface and receive no friend-assembly access. Core tests should prefer public contracts, but `Nanto.Core.Tests` may receive narrowly scoped friend access when direct verification of internal edge cases materially improves coverage or maintainability; that access must never become a production dependency.

### 5.2 Platform hosts

| Platform | Native application/window substrate | WebView | Initial status |
| --- | --- | --- | --- |
| Windows | Raw Win32 and `HWND` | Evergreen WebView2 | MVP |
| Android | .NET for Android bindings and `Activity` | Android `WebView` | Later host |
| iOS | .NET platform bind…14283 tokens truncated…ption | Phase 3 developer-experience prototype |
| O-014 | Exact browser telemetry package, default instrumentations, sampling, and relay wire format | Phase 3 telemetry prototype; account for experimental browser instrumentation status |

---

## 6. Windows host design

### 6.1 Chosen substrate

The Windows host uses:

- an STA UI thread;
- per-monitor-v2 DPI awareness established before any window is created;
- a standard Win32 message loop;
- registered Win32 window classes and owned `HWND` instances;
- WebView2 attached directly to the content area;
- CsWin32 or an equivalent source-generated P/Invoke layer for Win32 calls;
- explicit COM ownership suitable for Native AOT;
- the shared Evergreen WebView2 Runtime.

It does not require:

- WinUI 3;
- Windows App SDK;
- XAML;
- MAUI;
- WPF;
- WinForms;
- MSIX;
- a bundled Chromium/WebView2 Fixed Version runtime.

Windows 11 includes the Evergreen WebView2 Runtime, and Microsoft has distributed it to most eligible Windows 10 devices. Evergreen applications share the runtime; the application should detect the exceptional missing-runtime case and offer/bootstrap installation ([WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)).

### 6.2 Generated Win32 interop and the no-magic-number policy

Nanto should use [`Microsoft.Windows.CsWin32`](https://github.com/microsoft/CsWin32) as the default projection for ordinary Win32 APIs. CsWin32 reads Microsoft's [`win32metadata`](https://github.com/microsoft/win32metadata) and generates only the requested functions, structs, constants, enums, handle types, COM interfaces, supporting declarations, friendly overloads, and SafeHandle types into the consuming project. No CsWin32 runtime assembly ships with the application.

This is the intended answer to Win32 “magic numbers.” Microsoft deliberately converts many loosely typed integer-plus-constant patterns in the Windows headers into discoverable enums and records handle disposal metadata that projections can turn into SafeHandles. The generated code therefore allows Nanto's host to use names such as `WM_DPICHANGED`, `WM_GETMINMAXINFO`, `WINDOW_STYLE`, and typed `HWND` values instead of copied hexadecimal constants and hand-written `nint` signatures.

The Windows host should contain a curated `NativeMethods.txt`, requesting exact APIs and constants rather than entire Windows namespaces:

```text
RegisterClassEx
UnregisterClass
CreateWindowEx
DefWindowProc
DestroyWindow
GetActiveWindow
GetCurrentThreadId
GetMessage
GetModuleHandle
GetWindowRect
GetWindowText
IsWindowVisible
PeekMessage
PostMessage
PostThreadMessage
SetFocus
SetForegroundWindow
SetWindowPos
SetWindowText
ShowWindow
TranslateMessage
DispatchMessage
PostQuitMessage
GetDpiForWindow
SetProcessDpiAwarenessContext
WM_CREATE
WM_CLOSE
WM_DESTROY
WM_NCDESTROY
WM_SIZE
WM_DPICHANGED
WM_GETMINMAXINFO
WM_APP
PEEK_MESSAGE_REMOVE_TYPE
SET_WINDOW_POS_FLAGS
SHOW_WINDOW_CMD
WINDOW_EX_STYLE
WINDOW_STYLE
WNDCLASSEXW
```

CsWin32 generates transitive supporting types automatically. Keeping the input explicit makes the native surface reviewable and prevents accidental code growth.

Host code then remains readable and searchable against Microsoft's native documentation:

```csharp
HWND hwnd = PInvoke.CreateWindowEx(
    WINDOW_EX_STYLE.WS_EX_APPWINDOW,
    windowClass,
    title,
    WINDOW_STYLE.WS_OVERLAPPEDWINDOW | WINDOW_STYLE.WS_VISIBLE,
    x,
    y,
    width,
    height,
    default,
    default,
    module,
    null);

switch (message)
{
    case PInvoke.WM_DPICHANGED:
        return HandleDpiChanged(hwnd, wParam, lParam);

    case PInvoke.WM_GETMINMAXINFO:
        return HandleMinMaxInfo(hwnd, lParam);
}
```

The exact generated overloads can vary with CsWin32 configuration and target framework, but application code should retain this symbolic shape.

For Native AOT, CsWin32 must run before compilation so that .NET's own `LibraryImport` and `GeneratedComInterface` generators can process its output. The Windows host baseline is therefore:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>
  <DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
</PropertyGroup>
```

Microsoft's current guidance specifically recommends build-task mode plus disabled runtime marshalling for Native AOT ([CsWin32 getting started](https://microsoft.github.io/CsWin32/docs/getting-started.html), [Microsoft Win32 interop guidance](https://learn.microsoft.com/en-us/windows/apps/develop/interop/call-win32-apis)).

Interop policy:

- Generated declarations are `internal` implementation details of `Nanto.Hosting.Windows`.
- Nanto pins tested CsWin32 and Win32 metadata versions together and upgrades them through dedicated interop tests.
- Generated sources are inspectable in `obj/` but are not copied into application source or exposed as Nanto API.
- x64 compile/runtime tests validate structure sizes, pointer fields, callbacks, and calling conventions for the initial host. Equivalent native tests are required before adding a future architecture.
- Hand-authored `LibraryImport` declarations are permitted only for APIs unavailable or incorrectly represented in metadata, with a source link and ABI test.
- OS-defined constants must use generated symbolic names. Nanto-defined window messages are allocated centrally from `WM_APP`, represented by a named type/constant, and never repeated as raw numbers.
- Casting a generated enum or handle at a native boundary is acceptable; spreading primitive integers through host logic is not.

CsWin32 solves the Win32 declaration problem, but it must not be assumed to solve the separate WebView2 projection problem. WebView2 is distributed outside the core Windows SDK and has its own COM metadata and loader. As of August 2026, an open CsWin32 issue demonstrates failures while consuming `Microsoft.Web.WebView2.Core.winmd` directly ([CsWin32 issue 1695](https://github.com/microsoft/CsWin32/issues/1695)). Nanto therefore validates WebView2 independently through the interop-generation and real-host integration lanes.

The WebView2 Runtime is already native code; Native AOT compatibility is primarily a problem in the managed COM projection used by the Nanto host. .NET Native AOT does not support the built-in Windows COM interop system. Microsoft's supported AOT-friendly direction is source-generated `ComWrappers` using `GeneratedComInterface` and `GeneratedComClass` ([Native AOT limitations](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/), [ComWrappers source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation)). A projection based on conventional `ComImport`/runtime-generated wrappers is therefore not acceptable merely because it works under CoreCLR.

As of 9 August 2026, the current stable WebView2 SDK package is `Microsoft.Web.WebView2` 1.0.4129.50. Microsoft ships an AOT-oriented `Microsoft.Web.WebView2.Core.Projection` in recent Windows App SDK scenarios, but the public WebView2 documentation does not establish it as a supported standalone raw-Win32 C# Native AOT host independent of WinUI and Windows App SDK. Nanto must prove that use case rather than infer it from Windows App SDK support.

The WebView2 AOT comparison order is therefore:

1. Microsoft's current `Microsoft.Web.WebView2.Core.Projection`, consumed standalone from a raw-Win32 Native AOT host with no WinUI or Windows App SDK dependency.
2. A narrow Nanto-owned source-generated COM projection from Microsoft's official WebView2 IDL/header, containing only the interfaces, callbacks, enums, and methods Nanto uses.
3. CsWin32 against WebView2 metadata if the current metadata/projection issue is resolved and the output remains narrow and inspectable.
4. A hand-authored vtable projection only for gaps that cannot be represented correctly by the preceding approaches.

The selected path must publish with `DisableRuntimeMarshalling=true`, produce no unexplained trim/AOT warnings, and avoid pulling UI frameworks or broad Windows projection dependencies into the output. The WebView2 SDK version and generated ABI surface are pinned and reviewed together.

Phase 1 operationalizes this decision with the offline `eng/Nanto.WebView2InteropGen` tool. It reads `WebView2.idl` and `build/native/include/WebView2.h` directly from the centrally pinned `Microsoft.Web.WebView2` NuGet package, applies a committed narrow projection specification, and produces the reviewed `src/Nanto.Hosting.Windows/Interop/Generated/WebView2Interop.g.cs` plus a deterministic input/output manifest. Official SDK inputs are not copied into repository `artifacts/`; NuGet restore supplies them when regeneration or verification is explicitly requested.

The committed generated source is the only generator output consumed by production builds. `tests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests` regenerates into its ignored `obj/interop-verification/<Configuration>` directory and compares generated files byte-for-byte with the committed copies. Ordinary root builds neither execute a generator nor write source files. The detailed schema, commands, failure behavior, and test contract are specified in [`phase1-plan.md`](phase1-plan.md#webview2-interop-generation-and-verification).

System appearance uses the same offline-generation policy through `eng/Nanto.WinRtAppearanceInteropGen`. That generator reads the exact pinned Windows SDK targeting pack selected by the Windows TFM, validates the `UISettings` runtime/default-interface closure, complete `IUISettings3` prefix through the selected members, layouts, event token, and closed callback IID, then emits a raw `IInspectable` ABI and manifest. The historical comparison spike showed that this removes `WinRT.Runtime` from the Native AOT reachability graph while preserving behavior. Production owns Windows Runtime apartment initialization explicitly and does not reference the spike.

Nanto calls the loader through generated `LibraryImport` declarations shared by both compilation paths. CoreCLR loads the architecture-specific Microsoft-signed `WebView2Loader.dll` copied beside the host. Native AOT resolves the same entry points from the statically linked `WebView2LoaderStatic.lib`, producing no dynamic loader dependency. The standalone deployment spike proves both paths; the Evergreen WebView2 Runtime remains a separate installed system component ([static WebView2 loader](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/static)).

Third-party projects such as [WebView2Aot](https://github.com/smourier/WebView2Aot) are useful implementation references and test comparators, but should not become foundational dependencies without a separate API, maintenance, license, size, and teardown review.

### 6.3 Windows object ownership

```text
WindowsApplicationHost
├── UI thread and message loop
├── application cancellation source
├── shared WebView environment
└── WindowRegistry
    └── WindowsWindow
        ├── owned HWND
        ├── WindowMessageMonitor
        ├── WebView controller
        ├── WebView instance
        ├── IPC transport
        ├── event subscription tokens
        └── window/plugin lifetime scope
```

An object that borrows a resource must not release it. An object that owns a resource must release it exactly once.

### 6.4 Window lifecycle

Every window has an explicit state machine:

```text
Created → Initializing → Running → Closing → Closed
                  ↘ Failed ↗
```

Rules:

- Transitions are serialized on the UI thread.
- `CloseAsync` is idempotent.
- A second native close request is harmless.
- Native callbacks received after `Closing` begins either complete required teardown or are ignored safely.
- Failed initialization cleans up every resource that was successfully created.
- No command may begin after its window enters `Closing`.
- Outstanding window commands receive cancellation and a bounded shutdown interval.
- `Closed` is terminal.

### 6.5 Reverse-order teardown

The target shutdown sequence is:

1. Mark the window `Closing` and reject new IPC work.
2. Cancel the window lifetime token and active command streams.
3. Ask window-scoped plugins to stop.
4. Unsubscribe every browser, environment, and WebView event using its registration token; remove any registered scripts, mappings, or callbacks owned by the window.
5. Call `ICoreWebView2Controller::Close`, then release the WebView, controller, and callback COM interfaces.
6. Release the window's reference to shared WebView resources. After the last controller closes, use `BrowserProcessExited` to synchronize runtime replacement, environment reconfiguration, authentication-cache clearing, or user-data-folder deletion.
7. Remove native message hooks and callbacks.
8. Destroy the `HWND` if native close has not already done so.
9. Remove the window from the registry.
10. Dispose the window/plugin service scope.
11. Evaluate the application shutdown policy.

The same operations must be safe when entered from user close, application shutdown, renderer failure, failed initialization, or an OS callback.

### 6.6 Resource implementation rules

- Wrap suitable owned handles in `SafeHandle` subclasses.
- Use explicit wrappers for COM interfaces; do not depend on finalizer timing.
- Represent event registration with disposable subscription tokens.
- Keep WebView2 environment and controller callbacks on the owning STA UI thread. All requests into WebView2 must be marshalled to that dispatcher.
- Use `IDisposable` for synchronous ownership and `IAsyncDisposable` only when shutdown genuinely requires asynchronous completion.
- Do not block the UI thread waiting on work that must resume on the UI thread.
- Do not use `.Result`, `.Wait()`, or a native blocking wait around a WebView2 asynchronous operation; WebView2 completion callbacks require the message pump to continue running ([WebView2 threading model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model)).
- Keep a debug resource ledger containing windows, handles, COM objects, subscriptions, streams, and plugin scopes.
- Report unreleased resources at process exit in development builds.
- Catch only documented/understood native or COM errors at teardown boundaries; do not broadly swallow exceptions.
- Make failed startup testable by injecting a failure after every acquisition step.
- Test cancellation without turning it into injected failure: after each acquisition checkpoint, production must observe cancellation before performing the next acquisition.
- Start the shutdown deadline with the first stop request, including when a WebView2 environment or controller callback is still pending; public completion may time out, but callback ownership and the STA remain alive until late native completion can release or roll back safely.
- Exercise two complete processes against the same application/profile UDF in one parent-owned containment job; after one exits, the survivor must remain usable and the shared root must be deletable after both stop.

### 6.7 DPI and window messages

Nanto's portable API describes the client content size in device-independent pixels. The Windows host owns conversion to physical pixels based on the current window DPI and uses `AdjustWindowRectExForDpi` so native non-client chrome does not reduce the requested WebView content area. `INantoWindow.Size` is a thread-safe snapshot of the actual client size; `WM_SIZE` updates the snapshot and WebView controller from the same native client rectangle. When the Nanto-owned parent receives `WM_SETFOCUS`, the host transfers keyboard focus into WebView2 with `ICoreWebView2Controller.MoveFocus(Programmatic)` so activation and later focus restoration reach the web content rather than stopping at the frame window.

The private Windows UI thread enters Per-Monitor-V2 awareness before it creates its message queue or any `HWND`. Initial placement is automatic in Phase 1: Windows selects the screen position, then Nanto sizes the client area for the selected window DPI. On `WM_DPICHANGED`, Nanto updates its UI-thread-owned DPI before applying Windows' suggested rectangle. This preserves the intended apparent size without creating a second coordinate system.

Nanto deliberately does not expose programmatic screen positioning in Phase 1. [WPF's per-monitor model](https://learn.microsoft.com/en-us/windows/win32/hidpi/declaring-managed-apps-dpi-aware) and [Electron's screen API](https://www.electronjs.org/docs/latest/api/screen) support logical desktop placement with substantial display/conversion machinery, while [WinUI AppWindow](https://learn.microsoft.com/en-us/windows/apps/develop/ui/manage-app-windows), [Avalonia](https://api-docs.avaloniaui.net/docs/T_Avalonia_Controls_Window), and [winit](https://docs.rs/winit/latest/winit/window/struct.Window.html) keep native screen placement distinct from logical content sizing. A primary-DPI global coordinate rule would be ambiguous on mixed-DPI topologies, and native pixel coordinates do not belong in the portable contract. Application-directed placement therefore waits for a display model with stable display identity, physical bounds, working area, scale factor, and display-relative conversion.

At minimum, it must correctly handle:

- `WM_DPICHANGED`;
- `WM_SIZE` and WebView bounds updates;
- activation and focus;
- close and destroy;
- display/work-area changes;
- custom title-bar hit testing when that feature is added.

The host maintains cached per-window DPI and native placement only on the UI thread. Phase 1 leaves standard minimum sizing to `DefWindowProc`; a public minimum-size contract is deferred until a concrete application need justifies it. On `WM_DISPLAYCHANGE` and `WM_SETTINGCHANGE` for `SPI_SETWORKAREA`, Nanto enumerates every current monitor work area. Any positive rectangle intersection preserves the current normal placement. A wholly inaccessible normal window moves to the nearest work area, clamps each axis while preserving its native size, and aligns an oversized axis with that work area's origin. Coordinate ordering breaks equal-distance ties so enumeration order cannot change the result. For minimized and maximized windows, `GetWindowPlacement`/`SetWindowPlacement` retains the show state while Windows validates and corrects the normal restore rectangle in its documented workspace coordinate system. Because `GetWindowPlacement` clears its flags, Nanto tracks maximize/restore `WM_SIZE` transitions and reconstructs `WPF_RESTORETOMAXIMIZED` for a window minimized from maximized. Windows reports a hidden window as `SW_SHOWNORMAL`, so Nanto corrects its current screen rectangle through `SetWindowPos` without any showing or activation flags; the window remains hidden. Real initial-placement, cross-monitor, monitor-removal, and mixed-scale behavior require dedicated visible multi-monitor tests.

### 6.8 Static asset origin

Production assets must be served by an `IWebAssetProvider` abstraction. The Windows host should avoid a loopback HTTP server unless a platform limitation requires it.

The default production implementation materializes explicitly declared embedded SPA assets into a versioned local cache and maps that directory to `https://app.nanto.invalid` using `SetVirtualHostNameToFolderMapping`. The default access kind is `DenyCors`. This gives the application a secure origin, supports relative resources and browser storage, and lets WebView2 resolve files inside its own processes. The hostname is an internal implementation detail and must not leak into the frontend API.

`WebResourceRequested` remains a future option for genuinely dynamic or in-memory content. It is not part of Phase 1 and is not the default for a normal SPA because every intercepted resource crosses into the host UI thread and is slower than virtual-host mapping. A directory deployment can map its output directly; an embedded single-file deployment can extract once per asset version and then use the same serving path ([local content in WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content)).

The completed feasibility work established these requirements for Nanto:

- a secure application origin;
- exact declared-asset routing with client-side history and hash routing after startup;
- embedded or linked assets;
- correct MIME types;
- fetch, module, worker, and CSP behavior;
- no accidental filesystem access;
- atomic cache creation and safe retention across application upgrades;
- no extraction on every launch when the asset version is already present.

Every application declares a stable, globally unique `ApplicationId`, normally in reverse-DNS form such as `com.ghidello.myapp`. Nanto trims and canonicalizes it to lowercase invariant, limits each ASCII segment to 63 characters and the complete identifier to 253 characters, and derives a collision-resistant filesystem key. Display name, assembly name, executable name, and installation directory are not identities. Changing `ApplicationId` deliberately creates a new storage boundary.

The Windows cache schema is `%LOCALAPPDATA%\Nanto\applications\<application-key>\assets-v1\<bundle-sha256>`. The bundle key hashes the normalized asset manifest and contents rather than the application release number, so releases with identical assets reuse one immutable `content` directory. Bundle siblings record completeness and an explicit lease. The stable WebView2 UDF lives under the same application boundary at `profiles\default\webview2-udf`, outside release and bundle directories, so browser storage survives application upgrades.

Phase 1 always uses `%LOCALAPPDATA%\Nanto\applications\<application-key>` as its application data root. The root is write-probed before asset extraction or WebView2 startup, and failure reports the exact path without silently falling back elsewhere. Adjacent runtime configuration, command-line storage overrides, environment overrides, and enterprise storage policy are deferred until deployment requirements justify the additional configuration surface.

Each process holds a shared, non-deleteable handle to the selected bundle's `lease.lock` until mappings and the WebView2 controller are released. Multiple instances may hold the lease concurrently. A named per-application cross-process maintenance mutex serializes lease acquisition, structural validation, quarantine, and publication while `maintenance.lock` records the on-disk cache layout. The Windows mutex is machine-global and user-storage-scoped so separate sessions coordinate without coupling unrelated users. Reuse checks declared metadata, the exact regular-file inventory, and file lengths without rehashing every content file. A corrupt bundle belonging to the current application is atomically quarantined and reconstructed; an application-identity mismatch fails without modification even when other structural corruption is present. Phase 1 does not automatically delete old valid bundles, abandoned staging directories, or quarantined bundles. Age-based retention and cleanup remain future distribution work.

### 6.9 Application appearance

Nanto models color scheme as an application/profile-wide preference with `System`, `Light`, and `Dark` values. The application owns persistence of a user-selected value and supplies it on the next run; Nanto does not introduce a competing general settings store. On Windows, the host applies the preference to the WebView2 profile before initial navigation and supports live mutation on the owning STA thread.

WebView2 exposes the effective preference to browser chrome and web content through the standard `prefers-color-scheme` media feature. SPAs use CSS or `matchMedia` and need no Nanto-specific theme message, framework adapter, or JavaScript API. `System` continues to follow operating-system changes, while explicit values override them.

For native Win32 chrome, Nanto follows [Microsoft's supported desktop guidance](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-windows-themes): read the `Windows.UI.ViewManagement.UISettings` foreground color, classify its luminance, observe `ColorValuesChanged`, and apply the resolved result through `DWMWA_USE_IMMERSIVE_DARK_MODE`. A pinned offline generator projects only the required raw WinRT ABI, so the production assembly references neither the broad SDK C#/WinRT projection nor `WinRT.Runtime`, WinUI, or Windows App SDK. Native AOT removes `WinRT.Runtime` from the reachable image; an untrimmed self-contained CoreCLR Windows publish may still copy the complete platform runtime pack containing it. The UI thread owns and balances its Windows Runtime apartment; the application-level WebView owner owns the UISettings interface, native callback, and subscription, while each attached window owns a shorter registration that is removed after its WebView controller closes and before its `HWND` is destroyed.

Initial native appearance is applied after `HWND` creation but before WebView2 acquisition or showing the window. Explicit preference changes update the native frame and WebView2 profile on the owning STA thread and publish the portable preference only after both succeed; a WebView2 failure rolls the native frame back to the previous preference. `ColorValuesChanged` is marshalled to that STA and affects the native frame only while the portable preference is `System`; WebView2's `Auto` profile mode follows the same operating-system change independently. Callback failures are retained and reported during deterministic teardown. Nanto does not treat the undocumented `AppsUseLightTheme` registry value as an application contract. The resolved Light/Dark value remains internal, and the SPA observes it through web standards.

### 6.10 Renderer and process recovery

Each WebView owns a `ProcessFailed` subscription and removes it before the other WebView subscriptions during teardown. Nanto maps WebView2 values to the portable `RendererFailureKind`; public contracts and descriptions do not expose COM enums. The renderer event is raised synchronously on the owning STA before recovery begins, and handler failures are logged without changing the recovery decision.

A main-frame renderer exit or unresponsive notification receives at most one automatic `Reload` attempt during the window lifetime. The first event reports `WillAttemptRecovery = true` only while the window is still running and no close has been requested; a synchronous reload failure, failed recovery navigation, subsequent main-renderer failure, or failure during closure requests the window's ordinary close path. Browser-process exit cannot be repaired by reload, so it reports `Exited` with `WillAttemptRecovery = false` and closes normally. Because that process already destroyed its native state, teardown releases Nanto's logical subscription, mapping, controller, and COM ownership without issuing unavailable WebView2 mutations. A frame-only renderer exit reports `FrameRendererExited`; utility, sandbox, GPU, plugin, and unknown child-process exits report `Unknown`. Those isolated or runtime-recreated failures are diagnostic-only in Phase 1 and do not reload or close the primary window.

Recovery deliberately preserves the existing controller, profile, secure mapping, navigation policy, asset lease, appearance, and native window. Nanto does not build a controller/environment recreation state machine in Phase 1. The hidden integration fixture uses internal seams to invoke Chromium's `Page.crash` and to identify and terminate its isolated browser process. It proves the portable events, mapped-SPA reload, browser-loss closure, and zero final resources; neither seam is public application API.

### 6.11 Critical Native AOT risk: COM

WebView2 is COM-based, while .NET Native AOT on Windows does not provide built-in COM support. This is the most important technical risk in the Windows MVP.

Before building the framework, a feasibility spike must prove that Nanto can:

- create a WebView2 environment and controller from Native AOT;
- receive WebView2 callbacks;
- exchange web messages;
- resize, navigate, open developer tools, and shut down repeatedly;
- use generated or manually defined COM interop without reflection or runtime code generation;
- publish with zero unexplained trim/AOT warnings;
- run without leaked COM references or shutdown crashes.

Possible implementations include Microsoft's AOT projection when it is independently usable without Windows App SDK, source-generated COM interop from the official WebView2 IDL/header, and narrowly scoped manually generated ABI bindings for remaining gaps. The public host contract must not depend on the chosen mechanism.

#### Adopted feasibility evidence

The completed feasibility work established the following inputs to Nanto Phase 1. This is a self-contained evidence summary, not a dependency on experiment source, test profiles, or reports:

- A warning-free `win-x64` Native AOT host can use raw Win32, generated COM interop, `DisableRuntimeMarshalling=true`, and WebView2 without WinUI, Windows App SDK, WPF, WinForms, or MAUI.
- The same narrow host contracts can support CoreCLR and Native AOT while changing only deployment and loader mechanics.
- A secure virtual HTTPS origin can support exact-origin checks, CSP, modules, dedicated/shared workers, browser storage, and denial of unintended CORS, mixed-content, and filesystem access.
- A normalized manifest and content hashes can drive deterministic, immutable asset extraction, cache reuse, exact declared-asset navigation, and safe cleanup.
- Renderer termination, browser-process termination, same-process recreation, deterministic teardown, and bounded failure diagnostics are feasible.
- Closing the controller while keeping the STA message pump active until a bounded `BrowserProcessExited` notification permits deterministic WebView2 UDF cleanup.
- Native feasibility was demonstrated on x64 and Arm64. Phase 1 deliberately supports Windows x64 only; Arm64 remains future scope.
- CoreCLR and Native AOT measurements can record readiness, lifecycle time, process-tree memory, executable and installed payload sizes, symbols, and compressed payload consistently.

Nanto adopts only the production rules stated in this architecture and [`phase1-plan.md`](phase1-plan.md). No historical experiment path, test profile, report, or unstated behavior is a Nanto requirement.

---

## 7. Application and surface lifecycle

### 7.1 Portable lifecycle

Nanto should expose a small lifecycle vocabulary:

- `Creating`
- `Created`
- `Activated`
- `Deactivated`
- `Suspending` or `Stopping` where meaningful
- `Resuming`
- `Closing`
- `Closed`

Not every event exists on every platform. Platform hosts map native events and document guarantees. Application state must be saved incrementally because mobile platforms and system shutdown cannot guarantee a final callback.

### 7.2 Surface model

Windows and tray icons are peer application surfaces. This supports applications that close their last visible window but remain active in the tray.

The complete multi-surface model is Phase 6 work. Its suggested future shutdown policies are:

```csharp
public enum ShutdownMode
{
    OnPrimaryWindowClosed,
    OnLastSurfaceClosed,
    Explicit
}
```

Owned dialogs and auxiliary windows do not automatically become shutdown-defining surfaces.

Phase 1 exposes only `OnPrimaryWindowClosed` and `Explicit`, together with one nullable `PrimaryWindow`. It does not expose `OnLastSurfaceClosed` or a public window collection.

### 7.3 Thread affinity

- Each platform host captures its UI dispatcher/executor.
- All window and WebView mutation happens through that dispatcher.
- Public application/window state and property getters publish immutable snapshots that are safe to read from any thread; observation never requires dispatcher round-trips.
- Debug builds throw immediately when a UI-thread-only API is used incorrectly.
- The command dispatcher may execute ordinary commands away from the UI thread, but injects a portable UI dispatcher for operations that affect windows.
- Registries use immutable or copy-on-write snapshots for thread-safe observation; mutation remains UI-thread-owned.

---

## 8. IPC and typed bindings

Typed C# ↔ TypeScript bindings are a defining Nanto feature, not optional tooling.

### 8.1 C# authoring model

An ordinary API type exposes explicitly annotated methods and needs no type-level attribute. `[NantoApi]` marks only a composed group root; `[NantoApiPart<TApi>]` contributes independently constructed parts to that group:

```csharp
[NantoApi]
public sealed class ProjectsApi
{
    [NantoCommand]
    public ValueTask<NantoResult<ProjectDetails, OpenProjectError>> OpenAsync(
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        // Application logic
    }

}

[NantoApiPart<ProjectsApi>]
public sealed class ProjectBuildsApi
{
    [NantoCommand]
    public IAsyncEnumerable<NantoResult<BuildProgress, BuildError>> BuildAsync(
        ProjectId projectId,
        BuildOptions options,
        CancellationToken cancellationToken)
    {
        // Stream progress
    }
}
```

The generated `NantoBridgeConfiguration.Add(...)` overloads register application-owned instances. Nanto holds references but neither constructs nor disposes those services. An `Api` type suffix and an `Async` method suffix are removed from frontend names; CLR namespaces do not participate. Partial types aggregate automatically, while duplicate generated group/member names are compilation errors.

### 8.2 Generated output

The Roslyn incremental generator produces:

1. A reflection-free command registry and dispatcher.
2. Source-generated `System.Text.Json` metadata for every request, response, event, stream item, and structured error.
3. A protocol manifest containing commands, types, permissions, and plugin contributions.
4. A TypeScript model consumed by the SDK/CLI to emit `@nanto/app`.
5. Diagnostics for unsupported, ambiguous, or unsafe signatures.

The generated TypeScript should feel handwritten:

```typescript
import { projects } from "@nanto/app";

const project = await projects.open(projectId, { signal });

for await (const progress of projects.build(projectId, { signal })) {
  console.log(progress.message);
}
```

### 8.3 Type mapping

Initial mappings:

| C# | TypeScript/runtime representation |
| --- | --- |
| `Task<T>` / `ValueTask<T>` | `Promise<T>` |
| `CancellationToken` | Optional `AbortSignal` call option |
| `IAsyncEnumerable<T>` | `AsyncIterable<T>` |
| nullable reference/value | `T \| null`, with optionality defined separately |
| records/classes | generated TypeScript interfaces or types |
| enum | generated constant object plus matching string-union type |
| `NantoResult<T, TError>` | `{ ok: true, value: T } \| { ok: false, error: TError }` |
| `Guid` | string, validated at the native boundary |
| `DateOnly` | ISO date string |
| `DateTimeOffset` | ISO timestamp string |
| `byte[]` / `ReadOnlyMemory<byte>` | `Uint8Array`, with optimized binary transport when available |
| `void` / non-generic task | `Promise<void>` |

The contract must distinguish missing, `undefined`, and `null`. Naming and enum serialization rules must be deterministic and configurable only at a well-defined boundary.

### 8.4 Unsupported signatures

The generator should initially reject:

- overloaded command names;
- open generics;
- arbitrary object graphs without generated serialization metadata;
- delegates and expression trees;
- raw pointers and platform handles in portable commands;
- synchronous streaming abstractions;
- methods whose public contract depends on runtime type discovery.

Diagnostics should identify the exact parameter or return type and suggest a supported alternative.

### 8.5 Protocol model

The transport protocol supports:

- request/response commands;
- structured errors;
- cancellation;
- typed events;
- ordered streams/channels;
- window/WebView identity;
- capability context;
- protocol version negotiation.

Every request has a correlation ID. Responses are delivered exactly once from Nanto's perspective. Late responses after cancellation are discarded. Stream closure and error semantics are explicit.

The generator may assign compact numeric command IDs for the private wire format, as Wails v3 does, provided that:

- IDs are generated deterministically or recorded in the build manifest;
- collisions are detected at build time;
- the dispatcher is generated as a direct switch/table rather than a reflective lookup;
- capability documents and public TypeScript use symbolic service and command names;
- logs always show the symbolic name, with the numeric ID only as supplemental protocol detail;
- protocol-version mismatch fails clearly rather than invoking the wrong command.

The invocation context includes the calling application, window/WebView identity, origin, capabilities, request metadata, and a cancellation token. Closing or navigating the calling WebView automatically cancels its outstanding invocations unless a command has explicitly transferred work to an application-owned background operation.

Large binary payloads should not be base64-encoded JSON once a platform transport can carry bytes efficiently. Binary transport is a post-MVP optimization, but the protocol must reserve room for it.

### 8.6 Structured errors

Expected application failures are values expressed as `NantoResult<T, TError>`. Unexpected handler exceptions, transport, authorization, lifecycle, and protocol failures reject with `NantoCommandError`; caller cancellation rejects with the platform-standard `AbortError`. Native exception details and stack traces are never sent to frontends. The transport error codes are bounded symbolic values:

```typescript
type NantoCommandErrorCode =
  | "commandUnavailable"
  | "invalidRequest"
  | "protocolMismatch"
  | "resourceExhausted"
  | "internal";
```

Unknown and unauthorized commands intentionally produce the same `commandUnavailable` response. Unexpected failures include only the opaque request ID needed to correlate sanitized native logs.

---

## 9. Security and capabilities

Tauri explicitly treats the native core and frontend as different trust domains ([Tauri security model](https://v2.tauri.app/security/)). Nanto should do the same.

### 9.1 Default-deny rules

- Only generated and registered commands are callable.
- Installing a plugin does not automatically grant its permissions to every WebView.
- Every native request is associated with a known WebView, window, origin, command, and capability set.
- Navigating to a remote origin removes application capabilities unless explicitly granted.
- New windows receive an explicit capability profile.
- Development allowances must never be copied silently into production configuration.

### 9.2 Capability documents

A capability document grants named permissions to a set of windows/origins:

```json
{
  "identifier": "main-window",
  "windows": ["main"],
  "origins": ["https://app.nanto.invalid"],
  "permissions": [
    "app:default",
    "dialog:open",
    {
      "identifier": "filesystem:read",
      "allow": ["$APPDATA/projects/**"]
    }
  ]
}
```

The syntax and final application origin are provisional; the model is not.

### 9.3 Scope-aware plugins

Permissions can carry scopes:

- filesystem roots and glob patterns;
- allowed shell executables and arguments;
- URL schemes and domains;
- notification features;
- clipboard read versus write;
- database connection identifiers rather than arbitrary connection strings.

Plugins validate scopes again at the native boundary. The frontend cannot turn an allowed alias into a broader native operation.

### 9.4 WebView hardening

Production defaults should include:

- local application content only;
- navigation allowlists;
- explicit external-link handling;
- a restrictive Content Security Policy;
- no arbitrary native object injection;
- no unrestricted JavaScript evaluation from plugin input;
- disabled developer tools unless explicitly enabled for a diagnostic build;
- validation of message source and protocol envelope;
- no secrets embedded in frontend assets.

Microsoft recommends web messages rather than host objects for ordinary host/web communication. Nanto therefore uses `PostWebMessageAsJson`/`WebMessageReceived` for its generated protocol and does not use `AddHostObjectToScript` as its normal command bridge. Host objects are disabled by default. Every inbound message is accepted only after checking the current top-level origin and validating its generated protocol envelope; capability authorization is a separate, subsequent check ([WebView2 security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security), [WebView2 performance guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance)).

The WebView2 host runs at standard user integrity. An application that genuinely requires elevation isolates that privileged work from the WebView-hosting process rather than elevating the browser surface.

---

## 10. Plugin model

### 10.1 Plugins are build-time components

An Nanto plugin is normally a NuGet package selected at build time. It may contribute:

- portable managed services;
- platform-specific native implementations;
- generated command handlers;
- permissions and scope validators;
- TypeScript declarations/client methods;
- configuration schema;
- lifecycle hooks;
- packaging metadata or native assets.

The NuGet package is the source of truth. Nanto generates the matching frontend module so users do not need to coordinate independently versioned NuGet and npm plugin packages.

Plugins are not discovered by scanning assemblies at runtime. The generator creates a static registry containing exactly the referenced plugins.

### 10.2 AOT compatibility

Plugin compatibility is verified, not merely claimed. A plugin can declare metadata, but the SDK also runs .NET trim/AOT analyzers and checks referenced assemblies where possible.

Suggested compatibility levels:

```csharp
public enum RuntimeCompatibility
{
    NativeAot,
    CoreClr
}
```

Build modes:

```text
dotnet nanto build --runtime auto
dotnet nanto build --runtime native-aot
dotnet nanto build --runtime coreclr
```

- `auto` prefers Native AOT and falls back to CoreCLR when a verified dependency requires it. The CLI must prominently explain the plugin and size/runtime consequence.
- `native-aot` is strict and fails on any incompatibility or unexplained AOT warning. This is the recommended CI setting for applications that promise AOT.
- `coreclr` is an explicit compatibility build.

No build may silently suppress AOT warnings to claim compatibility.

### 10.3 Plugin lifecycle

Plugins may have application, window, or invocation scope. Each scope is owned and disposed by the corresponding Nanto object.

Lifecycle hooks are ordered and bounded:

```text
Configure → Start → Running → Stop → Dispose
```

- A plugin that fails during start causes already-started plugins to stop in reverse order.
- A plugin cannot indefinitely block shutdown.
- Window-scoped plugin instances stop before their WebView and window disappear if they depend on them.
- Plugin callbacks run on documented executors; UI-thread access must be requested explicitly.

### 10.4 Initial plugin catalogue

Core should remain narrow. Candidate first-party plugins are:

**MVP or early:**

- clipboard;
- native dialogs;
- filesystem;
- opener/default application;
- logging;
- operating-system/application information;
- single-instance activation.

**Next:**

- notifications;
- global shortcuts;
- menus and tray;
- shell/process execution with strict scopes;
- persistent key-value store;
- deep links;
- updater.

**Later or community:**

- SQL/database access;
- secure credential storage;
- HTTP with native policy integration;
- taskbar/dock integration;
- autostart;
- window-state persistence;
- platform-specific integrations.

Window management, lifecycle, IPC, capabilities, and asset serving remain core because plugins depend on them.

---

## 11. Developer experience

Developer experience is a release criterion for Nanto, not a convenience layer over the runtime. A feature is incomplete if its happy path is fast but its generated output, restart behavior, failure mode, or native cleanup is opaque. Phase 3 must establish measurable baselines for first-run time, incremental frontend feedback, managed restart time, diagnostic quality, and clean process termination; later phases may not regress them silently.

### 11.1 Project creation

The .NET-native entry point should be:

```bash
dotnet new install Nanto.Templates
dotnet new nanto -n MyApp --frontend react
cd MyApp
dotnet nanto dev
```

The initial reference template uses React, TypeScript, and Vite because it provides a familiar way to prove the complete experience. It is not the Nanto frontend model. The template system should also support `--frontend custom` (or `none`) so an existing or newly created SPA can be attached by configuring its commands, development URL, output directory, and generated-client directory. Additional curated templates can follow without changing the host or bridge.

Suggested project structure:

```text
MyApp/
├── MyApp.csproj
├── Program.cs
├── nanto.json
├── Capabilities/
│   └── main.json
├── Api/
│   └── AppApi.cs
└── Frontend/
    ├── package.json
    ├── vite.config.ts
    ├── src/
    └── generated/
        └── nanto/
```

Generated files should be consumable through the `@nanto/app` import alias but normally excluded from source control. A stable manifest may be checked in optionally for API review.

### 11.2 Configuration

Nanto needs one schema-validated application configuration containing:

- application identity and metadata;
- frontend directory, dev command, dev URL, and production output;
- initial windows;
- capability documents;
- runtime/build preference;
- plugin configuration;
- bundling/signing settings;
- development diagnostics.

Configuration should support environment-specific overlays without embedding secrets. MSBuild owns compilation and publish properties; `nanto.json` owns application behavior. The same value should not have two competing sources of truth.

Example:

```json
{
  "$schema": "https://nanto.dev/schemas/config/v1.json",
  "productName": "MyApp",
  "identifier": "com.example.myapp",
  "build": {
    "beforeDevCommand": "npm run dev",
    "beforeBuildCommand": "npm run build",
    "devUrl": "http://localhost:5173",
    "frontendDist": "Frontend/dist",
    "runtime": "auto"
  },
  "windows": [
    {
      "id": "main",
      "title": "MyApp",
      "width": 1100,
      "height": 720
    }
  ]
}
```

### 11.3 Development orchestration

`dotnet nanto dev` should:

1. Validate the .NET, Node/package-manager, native toolchain, and WebView prerequisites.
2. Restore/build generated C# and TypeScript contracts.
3. Start `beforeDevCommand` (for example Vite, Angular CLI, webpack, Rsbuild, Parcel, or another SPA development server).
4. Wait until `devUrl` is reachable instead of relying on arbitrary delays.
5. Start the native host under `dotnet watch`/CoreCLR.
6. Load `devUrl` into the WebView.
7. Forward structured logs from the host and frontend with clear prefixes.
8. Stop all child processes when the command exits.

Tauri uses the same `beforeDevCommand`, `devUrl`, `beforeBuildCommand`, and `frontendDist` concepts, with the selected frontend dev server providing HMR ([Tauri development configuration](https://v2.tauri.app/develop/), [Tauri Vite integration](https://v2.tauri.app/start/frontend/vite/)). Nanto should preserve that familiar, framework-neutral mental model.

### 11.4 Hot reload behavior

There are three independent change paths:

| Change | Expected behavior |
| --- | --- |
| SPA/CSS/frontend code | The selected frontend dev server's HMR or live-reload behavior updates the page without restarting the native host |
| C# method-body change supported by Hot Reload | `dotnet watch` applies the update in the running CoreCLR development process |
| C# shape/native interop/change requiring restart | CLI restarts the host and reports why |

When a C# API contract changes:

1. the incremental generator updates the manifest and TypeScript files;
2. the selected frontend watcher observes the generated-file change;
3. TypeScript reports broken frontend callers immediately;
4. the host is hot reloaded or restarted depending on the managed change.

Production Native AOT is not the inner development loop. The SDK keeps AOT analyzers enabled during ordinary builds, and CI performs strict AOT publishes. An optional `dotnet nanto dev --aot` may exist later as a slower validation mode, not as the default.

### 11.5 Diagnostics and developer tools

Development mode should provide:

- WebView developer tools enabled by default;
- a command and keyboard shortcut to open them;
- native debugger attachment instructions;
- generated protocol inspection;
- capability-denial explanations;
- child-process and HMR status;
- resource ledger/leak reporting on exit;
- `dotnet nanto doctor` for environment diagnostics.

Production developer tools are disabled unless the application explicitly opts in.

### 11.6 Aspire orchestration and OpenTelemetry

Aspire is a strong optional development host for Nanto. Its AppHost can orchestrate .NET projects, arbitrary executables, Vite applications, containers, databases, and other dependencies. Its dashboard receives OTLP telemetry and shows resources, logs, traces, and metrics together. Nanto should integrate with this model without making Aspire a runtime or production dependency ([Aspire executable resources](https://learn.microsoft.com/en-us/dotnet/aspire/app-host/executable-resources), [Aspire JavaScript integration](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/build-aspire-apps-with-nodejs), [Aspire telemetry](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/telemetry)).

The intended development modes are:

| Mode | Purpose |
| --- | --- |
| `dotnet nanto dev` | Minimal default loop: Nanto directly owns Vite and the managed host. No Aspire installation or AppHost is required. |
| Aspire AppHost | Opt-in orchestration for applications with APIs, databases, containers, queues, or a desire for the unified resource and telemetry dashboard. |
| Standalone Aspire dashboard | Optional OTLP viewer when full orchestration is unnecessary. |

The Aspire integration should eventually provide a small `Nanto.Hosting.Aspire` package with an `AddNantoApp(...)` resource extension. It should compose the Nanto host, frontend resource, readiness relationship, endpoints, dependent services, environment variables, and shutdown ordering while leaving the effective resource graph inspectable. Until that extension exists, an AppHost can use `AddProject` or `AddExecutable` for the Nanto host and the appropriate JavaScript resource—such as `AddViteApp`, `AddJavaScriptApp`, or another explicit executable—for the SPA.

Nanto core instrumentation should use standard .NET primitives—`ActivitySource`, `Meter`, and structured logging—so that instrumentation is cheap when no listener is present and export policy belongs to the application. An optional telemetry package may register the OpenTelemetry SDK and OTLP exporter. Aspire supplies the standard `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`, and `OTEL_EXPORTER_OTLP_ENDPOINT` settings during development; any OTLP-compatible collector can supply equivalent settings elsewhere. Telemetry must remain optional, trimmable, Native-AOT-tested, and free of secrets or command payloads by default.

The native host should emit spans and metrics for:

- application and window startup;
- WebView environment/controller creation and navigation;
- frontend bridge calls, including queueing, serialization, authorization, execution, cancellation, and failure;
- plugin startup, calls, and teardown;
- frontend and host restarts during development;
- renderer/process failure and recovery;
- shutdown phases and resource-ledger residue.

#### Telemetry from the WebView

Yes, frontend telemetry is possible. OpenTelemetry JavaScript supports browser traces and metrics, and the Aspire dashboard can receive browser telemetry over OTLP/HTTP. This instruments the SPA and Nanto bridge; it should not be presented as access to WebView2/Edge's private internal telemetry. Browser export cannot use OTLP/gRPC and must account for CSP, CORS, and endpoint authentication. OpenTelemetry currently labels browser client instrumentation experimental, so Nanto must keep this integration optional and versioned rather than making its present SDK surface part of Nanto core ([OpenTelemetry JavaScript status](https://opentelemetry.io/docs/languages/js/), [browser instrumentation](https://opentelemetry.io/docs/languages/js/getting-started/browser/), [browser exporters](https://opentelemetry.io/docs/languages/js/exporters/), [Aspire browser telemetry](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/enable-browser-telemetry)).

Nanto should support two export paths:

1. **Direct OTLP/HTTP during development.** The SPA exports to the Aspire dashboard or another collector using HTTP/protobuf or HTTP/JSON. The dev environment configures the exact WebView/Vite origin, CSP `connect-src`, CORS allow-list, and an ephemeral OTLP API key where required. Credentials must never be compiled into production assets.
2. **Nanto host relay as the preferred packaged-app path.** An optional `@nanto/telemetry` module sends telemetry batches through an internal, origin-checked bridge channel. The native host forwards them to the configured OTLP destination. This avoids browser CORS coupling and collector credentials in JavaScript, works with the production virtual HTTPS origin, and keeps export policy in native configuration.

All generated command envelopes should carry W3C Trace Context. A frontend interaction span can therefore parent the native bridge span, which can in turn parent database, HTTP, or plugin spans. Trace propagation is protocol metadata, not an application capability, but the internal telemetry channel remains restricted to trusted local origins and enforces size, rate, and attribute limits.

---

## 12. Build and distribution

### 12.1 Runtime profiles

| Profile | Managed runtime | WebView | Purpose |
| --- | --- | --- | --- |
| AOT | Native AOT, self-contained | Shared system/Evergreen WebView | Default and preferred production profile |
| CoreCLR compatibility, self-contained | Self-contained CoreCLR | Shared system WebView | Explicit production choice for non-AOT dependencies without requiring an installed .NET runtime |
| CoreCLR compatibility, framework-dependent | Installed CoreCLR | Shared system WebView | Explicit smaller deployment for environments that manage a compatible .NET runtime |
| Development | CoreCLR with Hot Reload | System WebView loading dev URL | Fast inner loop |
| Offline enterprise | AOT or CoreCLR | Runtime installer supplied separately/in bundle | Restricted environments |

The WebView runtime, application assets, symbols, fonts, and installer can dominate distribution size. Nanto must report both compressed download size and installed size rather than advertising only the executable size.

### 12.2 Size policy

- Do not bundle a WebView runtime in the normal profile.
- Do not reference unused platform UI frameworks.
- Treat trim and AOT warnings as errors in strict builds.
- Source-generate registration and serialization.
- Set `CopyOutputSymbolsToPublishDirectory=false` for production publishes so deployable application directories do not contain PDBs. Preserve generated symbols in a separate diagnostic artifact for CI evidence, crash-dump analysis, and releases that intentionally publish symbols; never disable symbol generation merely to make the deployable directory look smaller.
- Provide a build-size report grouped by managed code, native host, frontend assets, symbols, and installer payload.
- Establish size budgets from measured feasibility baselines rather than inventing a marketing number.

### 12.3 Packaging

The Windows MVP must first produce an unpackaged executable/application directory. Installer, MSIX, signing, update, and Store support are separate layers. Packaging must never be required merely to create a window or run WebView2.

WebView2 availability is an explicit deployment policy, informed by Wails' treatment of the same dependency:

```csharp
public enum WebViewRuntimePolicy
{
    RequireInstalled,
    DownloadBootstrapper,
    IncludeOfflineInstaller
}
```

The normal small profile uses `RequireInstalled` with an actionable error or `DownloadBootstrapper`. `IncludeOfflineInstaller` is an enterprise/offline packaging choice and its bytes are reported separately. Falling back to the user's external browser is not equivalent—the frontend would no longer have the same trusted window, origin, or IPC boundary—and is therefore not a transparent Nanto fallback.

Each platform host later owns its native packaging requirements while the CLI presents a coherent command surface.

---

## 13. Decision register

### 13.1 Accepted decisions

| ID | Decision | Rationale |
| --- | --- | --- |
| D-001 | Nanto is a .NET-native Tauri-like framework, not a literal port. | Preserve the product model while designing naturally for .NET. |
| D-002 | Use system WebViews. | Avoid bundling a browser engine and reduce application size. |
| D-003 | Keep the public application/window model platform-neutral. | Protect applications from OS-framework churn and support multiple hosts. |
| D-004 | Use raw Win32 + WebView2 for Windows. | Small, durable substrate with no WinUI/XAML/Windows App SDK requirement. |
| D-005 | Nanto owns the `HWND`; optional APIs attach to it. | The durable native resource remains under Nanto's control. |
| D-006 | Do not depend on MAUI UI, WinUI, or Reactor. | Avoid unnecessary framework weight and volatile abstractions. |
| D-007 | Learn from MAUI lifecycle/handler patterns and Reactor ownership/teardown patterns. | Reuse sound engineering lessons without importing their UI stacks. |
| D-008 | Native AOT is the preferred production target. | Smaller self-contained applications, startup performance, and architectural discipline. |
| D-009 | Provide CoreCLR compatibility mode for non-AOT-compatible plugins. | Avoid excluding useful .NET libraries while preserving a strict AOT path. |
| D-010 | Plugins are statically composed at build time. | Compatible with AOT, trimming, deterministic security, and small output. |
| D-011 | Typed generated C# ↔ TypeScript bindings are a defining feature. | Remove stringly typed IPC and make contract changes visible at build time. |
| D-012 | Use source-generated dispatch and JSON metadata. | Avoid reflection and enable trimming/AOT. |
| D-013 | Capability-based authorization is default-deny. | The frontend is a separate trust domain. |
| D-014 | Delegate frontend HMR to the selected SPA development server and orchestrate it alongside .NET Hot Reload/restart. | Preserve each frontend ecosystem's normal workflow; use Vite as the reference, not a dependency. |
| D-015 | Resource ownership and reverse-dependency teardown are architectural requirements. | Native lifetime bugs are correctness issues, not polish. Each scope releases dependents before prerequisites; unrelated application-, window-, and thread-scoped leases need not form one artificial global LIFO stack. Stable ledger lease IDs, external checkpoint processes, dependency-order assertions, and zero final counts provide the evidence. |
| D-016 | Use the Evergreen WebView2 Runtime in ordinary Windows distribution. | Share the installed runtime and avoid bundling Chromium. |
| D-017 | Windows is the first and only currently committed complete host. | It proves the product, smallest-host, and AOT/COM risks while platform-neutral contracts preserve—not promise—future options. |
| D-018 | Use CsWin32 plus Microsoft's Win32 metadata for ordinary Windows APIs. | Generate correct typed declarations, constants, handles, and cleanup metadata without shipping a wrapper runtime. |
| D-019 | Raw OS magic numbers are forbidden in host logic. | Use generated symbolic values; centralize and name Nanto-owned native message IDs. |
| D-020 | Treat Wails v3 as an additional architecture reference, not a dependency. | Its explicit objects, static bindings, services, assets, lifecycle, and transparent build model validate and refine Nanto's direction. |
| D-021 | Adopt **Nanto** as the product name and **“The native frame for your web application.”** as its tagline. | The Italian name expresses the framework, chassis, and weaving roles of the architecture while remaining concise and distinctive for developer tooling. |
| D-022 | The Native AOT WebView2 path must not use .NET's built-in COM interop. | Native AOT does not support built-in COM; use a proven AOT projection, source-generated COM, or a narrow generated ABI layer. |
| D-023 | Use JSON web messages, not host objects, for the normal generated frontend bridge. | Microsoft recommends web messages for performance, reliability, memory use, and reduced coupling; the design also fits capability checks and cross-platform hosts. |
| D-024 | Create and access WebView2 only on its owning STA UI thread, without blocking its message pump. | WebView2 callbacks and asynchronous completion depend on that thread and message pump. |
| D-025 | Share a WebView2 environment for compatible windows and make UDF ownership explicit. | Reduces browser-process memory and makes profile, update, cleanup, and shutdown behavior deterministic. |
| D-026 | Serve ordinary production SPA assets through virtual HTTPS host mapping from a directory or versioned extracted cache. | WebView2 resolves mapped resources in its own processes; per-request interception crosses the host UI thread and is reserved for dynamic providers. |
| D-027 | Synchronize WebView2 teardown through event-token removal, controller close, explicit COM release, and `BrowserProcessExited` where environment resources are involved. | Prevents reference cycles, shutdown races, UDF corruption, and failed runtime/configuration replacement. |
| D-028 | Run the WebView2 host non-elevated and validate origin before protocol and capability authorization. | Keeps the browser-hosting attack surface at standard-user integrity and preserves independent trust checks. |
| D-029 | Treat developer experience as a defining product surface and quality gate. | Fast feedback, transparent orchestration, actionable errors, diagnostics, and clean teardown determine whether the framework is genuinely usable. |
| D-030 | Support Aspire as an optional development orchestrator while instrumenting Nanto with vendor-neutral OpenTelemetry primitives. | Gains unified resource orchestration and local diagnostics without coupling the runtime or production deployment to Aspire. |
| D-031 | Commit only to Windows initially while keeping core contracts free of Windows types. | Focuses delivery while preserving the architectural option—not a promise—to add other hosts later. |
| D-032 | Propagate W3C Trace Context across the frontend bridge and support opt-in browser telemetry. | Produces end-to-end SPA-to-native-to-service traces while keeping browser SDK and export policy optional. |
| D-033 | Keep the runtime, generated ESM client, and frontend integration contract framework- and bundler-neutral. | React/Vite can provide the first polished template without restricting Angular, Vue, Svelte, Solid, vanilla TypeScript, or future SPA toolchains. |
| D-034 | Resolve Phase 1 navigation through an explicit manifest and exact declared-asset policy; never treat a missing resource as `index.html`. | Virtual-host mapping does not raise `WebResourceRequested`, so reliable URL-preserving fallback requires a future custom response-serving asset host. Client-side history and hash routing remain available after `/index.html` starts. |
| D-035 | Resolve O-001 with a narrow Nanto-owned source-generated WebView2 COM projection derived from the pinned official header. | Feasibility results establish Native AOT, trimming, callbacks, ABI layout, messaging, and teardown without a UI framework or built-in COM interop; deterministic regeneration verification prevents unnoticed projection drift. |
| D-036 | Resolve O-002 with the internal `https://app.nanto.invalid` origin and application-scoped, content-addressed asset caches with shared process leases. | Secure-origin behavior is proven; stable application identity prevents cross-application collisions, immutable bundles support concurrent releases, and leases prevent deletion while any instance is using a bundle. Automated age-based retention is deferred. |
| D-037 | Resolve O-003 by supporting Windows 10 22H2/build 19045 or newer on x64 for the initial product. | Completed feasibility and standalone deployment results establish the x64 path. Arm64 joins macOS and Linux as a future platform commitment rather than a Phase 1 gate. |
| D-038 | Resolve O-012 by making embedded, versioned extraction the default production asset deployment; retain directory mapping for development and externally managed assets. | Feasibility results establish single-file-compatible embedding, atomic extraction, secure virtual-host mapping, and content-hash cache reuse on Windows 10. |
| D-039 | Resolve O-006 by supporting explicit framework-dependent and self-contained CoreCLR compatibility publishes while keeping Native AOT the default. | Users with non-AOT dependencies need a deliberate fallback, but deployment ownership differs by environment. All modes use the same AOT-compatible host contracts and no mode is selected through silent publish fallback. |
| D-040 | Use only `%LOCALAPPDATA%\Nanto\applications\<application-key>` as the Phase 1 application data root. | A single deterministic location keeps the initial host and test protocol small. Configurable roots remain a future deployment feature rather than an undocumented override. |
| D-041 | Model color scheme as one application/profile-wide `System`, `Light`, or `Dark` preference and propagate it to SPAs through `prefers-color-scheme`. | Matches WebView2 profile semantics and web standards without a framework-specific frontend protocol. |
| D-042 | Applications persist user-selected appearance preferences; Nanto only applies them. | Avoids creating a partial settings subsystem or competing with application configuration. |
| D-043 | Derive the WebView2 projection from the complete base-interface chain and the required same-interface vtable prefix. | COM slot positions depend on both closures; projecting only named methods would produce an ABI-invalid interface even when every production call appears in the allowlist. |
| D-044 | Represent every acquired WebView2 interface as one uniquely owned source-generated COM wrapper and release it on the owning STA thread. | Explicit ownership prevents ambiguous RCW lifetimes, double release, thread-affinity violations, and Native AOT reliance on built-in COM interop. |
| D-045 | Keep virtual-host mapping for Phase 1 and defer clean-path reload fallback, service workers, custom response headers/MIME mappings, and external source maps. | Mapping keeps resource loading native and simple, but cannot intercept mapped requests or serve service-worker scripts. A future custom response-serving host is the robust upgrade; redirect plus history injection is rejected as fragile. |
| D-046 | Define portable window size as client-area DIPs and let the platform choose initial screen placement during Phase 1. | Content size remains portable and stable while native frame metrics vary by platform and DPI. Avoiding public X/Y prevents an ambiguous mixed-DPI global coordinate contract. |
| D-047 | Use Per-Monitor-V2 on Nanto's private Windows UI thread and accept the `WM_DPICHANGED` suggested rectangle. | Per-window DPI behavior remains isolated from the caller's threads, and the Windows-provided rectangle preserves apparent size across monitors without Nanto inventing topology transforms. |
| D-048 | Detect Windows System appearance through `UISettings` and `ColorValuesChanged`, not `AppsUseLightTheme`. | This follows Microsoft's supported Win32 guidance and avoids making an undocumented registry implementation detail part of Nanto's behavior. |
| D-049 | Recover native placement only when a window has no positive intersection with any current monitor work area. | Preserving partial visibility avoids surprising user-driven moves; nearest-work-area clamping restores an unreachable window without resizing it or exposing native placement through the portable API. |
| D-050 | Own system-appearance observation at application scope and attach native windows through shorter registrations. | One UISettings subscription matches the application/profile-wide preference, while releasing a window registration before `HWND` destruction prevents late color notifications from targeting stale native state. |
| D-051 | Attempt one in-place main-renderer reload per window lifetime; close for unrecoverable or repeated main-renderer failure. | This uses WebView2's documented renderer recovery without introducing controller/environment reconstruction, while preventing unbounded crash loops and keeping browser-process loss honest. |
| D-052 | Use stable source-generated structured logging with reserved subsystem event-ID ranges and opaque Nanto-owned fields. | Applications retain provider/exporter ownership while Nanto supplies allocation-conscious lifecycle diagnostics without duplicating the resource ledger or exposing identities, routes, paths, frontend data, or file contents. Swallowed application callback exceptions retain their diagnostic stack; returned framework/native failures are represented by bounded type, stage, operation, and numeric code. |
| D-053 | Select TestApp deployment through one explicit `NantoBuildMode` and isolate each mode beneath its own project-local `obj` and `bin` trees. | Framework-dependent CoreCLR, self-contained CoreCLR, and Native AOT must not reuse outputs or silently fall back between deployment contracts; configuration and test inventory remain independent concerns. |
| D-054 | Produce deployment evidence from a dedicated test-only inspector and keep deployables, symbols, packages, and evidence in separate ignored trees. | Release publishes remain product-shaped and free of PDBs, while deterministic hashes, ZIP size, loader trust, PE imports, and critical smoke results remain reproducible engineering evidence without adding production deployment APIs or machine-specific tools. |
| D-055 | Resolve the generated `WebView2Loader` imports statically for Native AOT through `DirectPInvoke` and the pinned package's `WebView2LoaderStatic.lib`. | The final native executable exercises the same production loader declarations without shipping or importing `WebView2Loader.dll`; the static library remains a link input rather than a deployment file, while CoreCLR retains the Microsoft-signed adjacent DLL. |
| D-056 | Project the documented `UISettings` appearance surface through a pinned generated raw `IInspectable` ABI and explicitly own the UI thread's Windows Runtime apartment. | The tracked comparison preserved appearance behavior while removing `WinRT.Runtime` and 1.794 MiB from the otherwise identical Native AOT spike; narrow generated metadata validation and deterministic ownership avoid replacing that dependency with handwritten ABI. |
| D-057 | Collect Phase 1 lifecycle-soak evidence through sequential isolated TestApp processes under one shared application identity and kill-on-close process group. | Process isolation exposes exit and storage-release failures, identity reuse exercises bundle/UDF reuse, containment bounds cancellation, and one ignored atomic summary preserves evidence without adding production soak behavior or committing machine-specific bulk artifacts. |
| D-058 | Close Phase 1 implementation while retaining the complete 550-process soak and mixed-DPI visible run as explicit acceptance follow-ups. | Both test systems and their production paths are implemented. A 397-process clean partial soak and passing single-monitor visible run provide useful evidence, while the remaining duration and hardware constraints need not block Phase 2 contract work. Neither pending result may be described as passed. |
| D-059 | Use method-level `[NantoCommand]` as the ordinary authoring surface and reserve `[NantoApi]` plus `[NantoApiPart<TApi>]` for composed groups. | Keeps simple APIs annotation-light while allowing large groups to be split across independently owned services without string names or runtime discovery. |
| D-060 | Model expected failures as `NantoResult<T, TError>` and sanitize all exceptional failures into bounded transport codes. | Preserves exhaustive application-domain errors without leaking exception implementation details across the trust boundary. |
| D-061 | Derive private 32-bit member IDs from SHA-256 canonical signatures and bind protocol v1 sessions to a full SHA-256 manifest fingerprint. | Provides compact dispatch with deterministic collision detection and prevents mismatched clients from invoking the wrong contract. |
| D-062 | Generate TypeScript as the frontend source of truth and compile JavaScript, declarations, and source maps mechanically with a pinned compiler. | Avoids two emitters drifting while supporting both TypeScript and JavaScript consumers. |

### 13.2 Recommended decisions awaiting implementation proof

| ID | Recommendation | Proof required |
| --- | --- | --- |
| R-002 | Make NuGet the plugin source of truth and generate frontend plugin modules. | Package-consumer ergonomics and JS bundler compatibility. |
| R-003 | Default `--runtime auto`, with strict `native-aot` for CI. | Ensure fallback is visible and never surprising. |
| R-005 | Use application/window/plugin lifetime scopes without a heavy mandatory DI dependency. | AOT size and ergonomics benchmark. |
| R-007 | Provide an inspectable MSBuild execution plan and CLI dry-run. | Confirm the build remains customizable without exposing unstable internal targets. |
| R-009 | Prefer a native OTLP relay for packaged WebView telemetry, while allowing direct OTLP/HTTP in development. | Prove batching, correlation, AOT size, origin checks, limits, shutdown flushing, and interoperability with Aspire Dashboard and a generic collector. |

### 13.3 Open decisions

| ID | Question | When to decide |
| --- | --- | --- |
| O-004 | macOS AppKit versus Mac Catalyst host | Before Apple host implementation |
| O-005 | GTK major version and supported Linux distributions | Before Linux host implementation |
| O-007 | Exact configuration and capability schema | Before CLI/template stabilization |
| O-008 | Stable command/event naming and TypeScript mapping rules | Generator prototype review |
| O-009 | Binary IPC transport | After JSON IPC MVP benchmark |
| O-010 | Packaging, signing, updater, and Store scope | After Windows MVP |
| O-011 | Numeric command-ID derivation and protocol compatibility strategy | Generator/IPC prototype benchmark |
| O-013 | Exact `Nanto.Hosting.Aspire` resource API and whether templates offer an `--aspire` option | Phase 3 developer-experience prototype |
| O-014 | Exact browser telemetry package, default instrumentations, sampling, and relay wire format | Phase 3 telemetry prototype; account for experimental browser instrumentation status |

### 13.4 Work explicitly deferred from Phase 1

| ID | Deferred work | Reconsider when |
| --- | --- | --- |
| F-001 | Public multi-window collection, `OnLastSurfaceClosed`, peer-surface ownership, and related shutdown policies. Phase 1 exposes only `PrimaryWindow`; the Windows host may keep an internal registry. | Phase 6 multi-window and tray work. |
| F-002 | Public borrowed native-window-handle access such as `IWindowsWindowHandle`. | A plugin or integration has a concrete `HWND` consumer and its ownership/lifetime contract can be tested. |
| F-003 | Publishing `Nanto.Testing` as a supported product package. Phase 1 keeps the utilities in a non-packable repository-local support assembly. | After the first host implementation proves the fake, dispatcher, recorder, and failure-plan shapes useful to external consumers. |
| F-004 | Adjacent `nanto.runtime.json`, `--data-root`, environment overrides, and enterprise-configurable application storage. | Phase 5 distribution work or an earlier concrete deployment requirement. |
| F-005 | Automatic age-based removal of old asset bundles, abandoned staging directories, and quarantined bundles, including retention customization. | Phase 5 distribution work, after real upgrade and rollback behavior supplies safe retention evidence. |
| F-006 | Multiple WebView2 runtime lanes in the integration protocol. Phase 1 uses Evergreen only and carries no redundant runtime-lane field. | A Fixed Version or other runtime lane becomes an active product or compatibility gate. |
| F-007 | Running the exhaustive framework-dependent behavioral/failure matrix again under self-contained CoreCLR and Native AOT. Phase 1 uses focused deployment smoke suites for those modes. | Mode-specific failures, release evidence, or risk justify the additional execution time and duplicate coverage. |
| F-008 | Full content rehashing whenever a published asset bundle is reused. Phase 1 trusts the current-user storage boundary and detects metadata, inventory, and length corruption rather than same-length external mutation. | The integrity model includes hostile or untrusted local modification. |
| F-009 | Forced stable-storage flushes and recovery guarantees across sudden power loss during asset publication. | Power-loss durability becomes a product or deployment requirement. |
| F-010 | Generated embedded-asset manifests and SDK/build integration. Phase 1 uses an explicitly reviewed manifest. | The SDK/tooling milestone defines the frontend build and embedding pipeline. |
| F-011 | Mutation watching or continuous inventory enforcement for directory-backed development assets. | Development tooling needs live invalidation beyond a fixed prepared URL inventory. |
| F-012 | Public display enumeration, stable display identity, display-relative positioning, and logical/native coordinate conversion. Phase 1 supports automatic initial placement and user-driven movement across displays. | An application needs deterministic placement or persisted restoration on a selected display, and the contract can represent topology changes without ambiguous global DIPs. |

---

## 14. Implementation roadmap

### Pre-Nanto feasibility and size gates (completed)

**Purpose:** prove the riskiest assumptions before designing broad APIs. The completed work established that:

- a raw-Win32 x64 host can embed WebView2 under CoreCLR and warning-free Native AOT without a managed UI framework;
- generated Win32 and WebView2 interop can preserve ABI correctness with runtime marshalling disabled;
- WebView2 messaging, secure-origin asset loading, SPA routing, recovery, and deterministic teardown work through the proposed ownership model;
- architecture-specific dynamic and static loader strategies are viable for CoreCLR and Native AOT respectively;
- repeated lifecycle tests and complete process-tree measurements can enforce resource and size budgets;
- x64 is ready for production implementation while Arm64 remains a future platform gate.

This completed stage is evidence for the chosen direction, not a source dependency. Nanto's normative implementation, verification, and failure requirements begin with Phase 1 and are fully stated in [`phase1-plan.md`](phase1-plan.md).

### Phase 1 — Windows host and lifecycle kernel (implementation complete)

Deliverables:

- `Nanto.Core` lifecycle and host contracts;
- `Nanto.Hosting.Windows` application host, UI dispatcher, message pump, window registry, and WebView host;
- curated CsWin32 API/constant inputs, offline WebView2 and WinRT-appearance interop generators, committed generated outputs and manifests, and byte-for-byte regeneration included in the complete unattended test gate;
- explicit application/window state machines;
- reverse-order cleanup registry for partial initialization;
- DPI-correct sizing and multi-monitor behavior;
- navigation policy and production asset provider;
- structured logging and resource ledger;
- non-packable repository-local fake host and dispatcher support for lifecycle unit tests.

Exit criteria:

- repeated and concurrent close paths are idempotent;
- injected failure at every initialization step releases prior resources;
- UI-thread violations fail clearly in development;
- renderer failure produces a controlled lifecycle event rather than a process crash.

All implementation exit criteria are satisfied. The complete 550-process soak and a passing mixed-DPI visible run remain acceptance follow-ups documented in [`phase1-gate.md`](phase1-gate.md); they do not block Phase 2 implementation and are not waived or reported as passed.

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
| Windows fast | DIP conversion, message decoding, ownership and cleanup-stack behavior, including bounded hidden raw-Win32 windows where they provide direct native-boundary coverage |
| Windows integration | Real HWND/WebView2, focus, resize, navigation, renderer failure, close races |
| AOT | Strict publish and smoke tests for every sample/plugin combination |
| Security | Origin spoofing, malformed messages, unauthorized commands, scope escapes, navigation |
| Dev loop | Generic dev-server readiness, HMR/live reload, generated-binding update, host restart, process cleanup |
| Telemetry | W3C context propagation, frontend/native span parenting, redaction, sampling, batching, relay limits, shutdown flush, disabled-listener overhead |
| Aspire integration | Resource readiness/order, frontend and host endpoints, dependent-service references, dashboard OTLP, Ctrl+C cleanup |
| Packaging | Clean VM/machine, WebView runtime present/missing, signed/unsigned paths |

During early Windows-host development, the Windows fast-test layer may create short-lived hidden raw-Win32 windows on private STA threads so ordinary fast tests exercise the real message and ownership boundary. These tests must not show or activate a window, start WebView2 or an external process, require desktop interaction, or become long-running. Once the hidden integration project and its external TestApp exist, measure the default suite and reconsider this placement: move the hidden-window cases out of the fast suite when doing so provides a material execution-speed or isolation benefit without leaving important ownership paths uncovered, and avoid retaining duplicate coverage without a specific reason.

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

Begin Phase 2 with the smallest vertical protocol slice while preserving the completed Phase 1 host contracts:

1. Specify the versioned request/response envelope, error shape, cancellation identity, and origin/window binding.
2. Prototype `[NantoApi]`/`[NantoCommand]` contracts and the incremental generator without runtime assembly scanning.
3. Generate deterministic dispatcher, source-generated JSON metadata, and framework-neutral ESM/TypeScript bindings for one representative command.
4. Exercise the slice through the existing secure WebView message boundary under CoreCLR and Native AOT, including malformed, unauthorized, cancellation, and teardown paths.
5. Continue to require separate approval for the two Phase 1 acceptance follow-ups and update [`phase1-gate.md`](phase1-gate.md) when either is collected.

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
