# Phase 1 — Windows Host and Lifecycle Kernel

## Summary

Phase 1 converts the Phase 0 evidence into production framework foundations using a clean rewrite. The evidence remains in the historical [Telaio `phase_1` branch](https://github.com/ghidello/telaio/tree/phase_1), sourced from commit [`0344107`](https://github.com/ghidello/telaio/commit/0344107c06ff36d2f89189bbb7660a46180193b1). Nanto production projects will not reference Phase 0 assemblies or require the Telaio repository to build.

Work will use small vertical commits and structurally separated test projects:

- `dotnet test` runs only production fast tests.
- Phase 0 remains independently reproducible from the Telaio repository.
- Hidden, AOT, visible, and long-running tests run by directly invoking their dedicated projects.
- Phase 1 will not use test profiles, traits, or filters to select test categories.

The integration strategy remains hybrid: pure tests for most behavior, hidden production-path WebView2 tests for unattended execution, and a small isolated visible-window project.

## Solution organization

### Root `Nanto.slnx`

This becomes the canonical production solution and contains only:

- `Nanto.Core`
- `Nanto.Hosting.Windows`
- `Nanto.Testing`
- `Nanto.Core.Tests`
- `Nanto.Hosting.Windows.Tests`
- `Nanto.Testing.Tests`

Therefore:

```powershell
dotnet build
dotnet test
```

build production code and run fast tests without creating WebView2 processes or windows.

### Historical Phase 0 boundary

Phase 0 source, tests, profiles, reports, and experiments stay in Telaio and are not copied into Nanto. Nanto records the decisions it adopts, but its build, tests, and release artifacts remain completely independent of the historical repository.

### Explicit Phase 1 integration projects

These projects are not included in root `Nanto.slnx`. They are invoked directly, and running one project runs all of its tests:

- `Nanto.Hosting.Windows.HiddenIntegrationTests`
- `Nanto.Hosting.Windows.AotIntegrationTests`
- `Nanto.Hosting.Windows.VisibleIntegrationTests`
- `Nanto.Hosting.Windows.LongRunningIntegrationTests`

No aggregate “all tests” solution is added during Phase 1 because it would make accidental desktop-affecting execution too easy.

## Architecture and contracts

Create:

- `Nanto.Core`: portable lifecycle, application, window, dispatcher, asset, and failure contracts.
- `Nanto.Hosting.Windows`: STA host, Win32 window, WebView2, assets, navigation, DPI, logging, and teardown.
- `Nanto.Testing`: fake host, fake dispatcher, failure scripting, and lifecycle assertions.

Initial portable API:

- `ApplicationState`: `Creating`, `Created`, `Activated`, `Deactivated`, `Closing`, `Closed`, `Failed`.
- `WindowState`: `Created`, `Initializing`, `Running`, `Closing`, `Closed`, `Failed`.
- Immutable GUID-backed `WindowId`.
- DIP-based `WindowBounds`.
- `WindowOptions` for title, initial bounds, visibility, resizability, and route.
- `ShutdownMode`: primary-window, last-surface, or explicit shutdown.
- `INantoWindow`: state, title, bounds, activation, close, and renderer-failure notification.
- `IUiDispatcher`: access check and asynchronous invocation without synchronous UI waits.
- `INantoApplicationHost`: state, dispatcher, immutable window snapshot, lifecycle events, single-use run, and idempotent stop.
- `IWebAssetProvider`: prepares an immutable versioned asset lease.
- Optional `IWindowsWindowHandle` in the Windows assembly.

Phase 1 supports one primary window. Multi-window and tray behavior remain later phases.

## Implementation specification

### Project graph and build properties

Use the following exact project layout:

| Project | Target | References and role |
| --- | --- | --- |
| `src/Nanto.Core/Nanto.Core.csproj` | `net10.0` | Portable contracts and implementations; references `Microsoft.Extensions.Logging.Abstractions` 10.0.10; sets `IsAotCompatible=true`. |
| `src/Nanto.Hosting.Windows/Nanto.Hosting.Windows.csproj` | `net10.0-windows10.0.19041.0` | References Core, pinned WebView2 SDK assets, CsWin32, and logging abstractions; supports `win-x64`; enables unsafe code, trimming analysis, `DisableRuntimeMarshalling`, and CsWin32 build-task generation. Product policy requires Windows 10 22H2/build 19045 or newer even though the Windows SDK contract version remains 19041. |
| `src/Nanto.Testing/Nanto.Testing.csproj` | `net10.0` | References Core only; contains deterministic fakes and assertions without Windows types. |
| `tests/Nanto.Core.Tests/Nanto.Core.Tests.csproj` | `net10.0` | References Core and the standard repository test packages. |
| `tests/Nanto.Hosting.Windows.Tests/Nanto.Hosting.Windows.Tests.csproj` | `net10.0-windows` | References Core and Windows hosting; contains no real window or WebView creation. |
| `tests/Nanto.Testing.Tests/Nanto.Testing.Tests.csproj` | `net10.0` | References Core and Testing. |
| `tests/Nanto.Hosting.Windows.TestApp/Nanto.Hosting.Windows.TestApp.csproj` | `net10.0-windows` executable | References Core and Windows hosting; external process used by every native integration project; publishable as CoreCLR or Native AOT. |
| `tests/Nanto.Hosting.Windows.IntegrationTestKit/Nanto.Hosting.Windows.IntegrationTestKit.csproj` | `net10.0-windows` | Shared process runner, request/report models, artifact handling, job-object containment, and assertions; it is not a test project. |
| Each `*IntegrationTests` project | `net10.0-windows` | References IntegrationTestKit and builds or publishes TestApp according to its single fixed execution model. |

Keep WebView2 and CsWin32 at the versions already pinned by Phase 0 until an explicit dependency change is reviewed. Add only [`Microsoft.Extensions.Logging.Abstractions` 10.0.10](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/10.0.10) to central package management. Do not add `Microsoft.Extensions.Hosting`, a DI container, OpenTelemetry SDK, or a UI framework in Phase 1.

The root solution contains the three production projects and three fast test projects only. TestApp, IntegrationTestKit, and every integration project are intentionally absent and are built through direct project commands.

TestApp declares the `win-x64` runtime identifier, defaults to CoreCLR for ordinary builds, and enables `PublishTrimmed`, full trimming, Native AOT, invariant globalization, and complete AOT analysis only when it is published by the AOT integration project. The AOT integration project owns an MSBuild target that publishes TestApp once to `artifacts/phase1/publish/win-x64` before test discovery. No test chooses the runtime through a profile property. Arm64 is outside Phase 1 and will require a future platform gate rather than conditional code in the initial host.

### Portable types and validation

Place public contracts under the `Nanto` namespace. Public Windows-only contracts use `Nanto.Hosting.Windows`.

```csharp
public enum ApplicationState
{
    NotStarted,
    Creating,
    Created,
    Activated,
    Deactivated,
    Failed,
    Closing,
    Closed,
}

public enum WindowState
{
    Created,
    Initializing,
    Running,
    Failed,
    Closing,
    Closed,
}

public enum ShutdownMode
{
    OnPrimaryWindowClosed,
    OnLastSurfaceClosed,
    Explicit,
}

public readonly record struct WindowId(Guid Value)
{
    public static WindowId Create() => new(Guid.NewGuid());
}

public readonly record struct WindowBounds(double X, double Y, double Width, double Height);
```

`WindowBounds` uses DIPs. Construction and mutation reject non-finite values and non-positive width or height with `ArgumentOutOfRangeException`; negative X/Y values are valid for monitors left of or above the primary monitor. `WindowId.Create` must never return `Guid.Empty`.

`WindowOptions` is a sealed record with:

- required non-empty `Title`;
- `InitialBounds`, defaulting to `(100, 100, 1024, 768)` DIPs;
- `StartVisible=true`;
- `Resizable=true`;
- `InitialRoute="/"`, which must be a root-relative route and must reject absolute, scheme-relative, backslash-containing, or traversal paths.

`NantoApplicationOptions` is a sealed record with:

- required stable `ApplicationId`, normally reverse-DNS form such as `com.ghidello.myapp`;
- required `PrimaryWindow`;
- required `Assets`;
- `ShutdownMode=OnPrimaryWindowClosed`;
- optional `ILoggerFactory`, defaulting internally to `NullLoggerFactory.Instance`;
- `ShutdownTimeout=15 seconds`, required to be positive and no greater than five minutes.

`ApplicationId` is canonicalized case-insensitively and must contain at least two dot-separated ASCII segments made from letters, digits, and interior hyphens. It is a persistent storage and security boundary, not a display label. Nanto derives `<application-key>` from the final readable segment plus the first 128 bits of SHA-256 over the canonical UTF-8 identifier, stores the complete identity in cache metadata, and refuses a metadata mismatch. Renaming an executable, assembly, or product display name does not change this identity; changing `ApplicationId` intentionally starts with a new cache and WebView2 profile.

### Portable interfaces

Use these public shapes; overloads may be implemented in the same types but their semantics may not differ:

```csharp
public interface IUiDispatcher
{
    bool CheckAccess();
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default);
    ValueTask InvokeAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);
    ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default);
}

public interface INantoWindow
{
    WindowId Id { get; }
    string Title { get; }
    WindowBounds Bounds { get; }
    WindowState State { get; }
    bool IsVisible { get; }
    event EventHandler<WindowStateChangedEventArgs>? StateChanged;
    event EventHandler<RendererFailedEventArgs>? RendererFailed;
    ValueTask SetTitleAsync(string title, CancellationToken cancellationToken = default);
    ValueTask SetBoundsAsync(WindowBounds bounds, CancellationToken cancellationToken = default);
    ValueTask ActivateAsync(CancellationToken cancellationToken = default);
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

public interface INantoApplicationHost : IAsyncDisposable
{
    ApplicationState State { get; }
    IUiDispatcher Dispatcher { get; }
    IReadOnlyList<INantoWindow> Windows { get; }
    event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;
    Task RunAsync(NantoApplicationOptions options, CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public interface IWebAssetProvider
{
    ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default);
}

public readonly record struct WebAssetPreparationContext(string ApplicationId, string ApplicationStorageKey);

public interface IWebAssetLease : IDisposable
{
    string RootDirectory { get; }
    string Version { get; }
    IReadOnlySet<string> AssetPaths { get; }
}
```

The host constructs `WebAssetPreparationContext` only after validating and canonicalizing `NantoApplicationOptions.ApplicationId`; providers must not independently derive a different application key. A directory provider may ignore the storage key, while the versioned provider uses it beneath the platform cache root.

State-change event arguments contain old state, new state, timestamp, and an optional failure object. Events are raised synchronously on the UI thread after the state field changes. Event handlers are diagnostic notifications: an exception from one handler is logged and does not prevent later handlers or teardown.

`RendererFailedEventArgs` contains a portable `RendererFailureKind`, a diagnostic description, whether recovery will be attempted, and the occurrence timestamp. It exposes no WebView2 enum or COM value.

`IWindowsWindowHandle.Hwnd` is `nint`, is valid only until the window enters `Closed`, and is documented as borrowed: consumers must never destroy or release it.

### Dispatcher semantics

- The Windows dispatcher is bound to the dedicated STA thread and uses a private `WM_APP` message to drain a FIFO queue.
- `CheckAccess` compares the current managed thread ID with the owning UI thread ID.
- Calls made on the UI thread execute inline. Calls from other threads enqueue work and return an incomplete `ValueTask`.
- Cancellation before execution removes or skips the queued callback and completes it with `OperationCanceledException`. Once execution begins, cancellation is only passed to asynchronous callbacks and cannot interrupt synchronous work.
- Callback exceptions are propagated to the returned task. Continuations are completed asynchronously so they cannot re-enter the native callback stack.
- Once application state reaches `Closing`, new dispatcher work fails with `ObjectDisposedException`; work already running receives the application lifetime cancellation token.
- The UI thread must never call `.Wait()`, `.Result`, `GetAwaiter().GetResult()`, `WaitHandle.WaitOne`, or a native blocking wait for work that can require its message pump.

### Lifecycle rules

Application transitions are restricted to:

```text
NotStarted → Creating → Created
Created/Deactivated → Activated
Activated → Deactivated
NotStarted/Creating/Created/Activated/Deactivated → Failed → Closing → Closed
Creating/Created/Activated/Deactivated → Closing → Closed
```

Window transitions are restricted to:

```text
Created → Initializing → Running → Closing → Closed
Created/Initializing/Running → Failed → Closing → Closed
Created/Initializing → Closing → Closed
```

Any transition not listed above throws `InvalidOperationException` in tests and debug builds. `Closed` is terminal.

- `RunAsync` is single-use and returns only after `Closed`. A second invocation throws `InvalidOperationException`.
- Cancellation of the `RunAsync` token requests orderly shutdown; it does not abandon cleanup. `RunAsync` completes normally if shutdown succeeds.
- `StopAsync` and `CloseAsync` are idempotent and safe concurrently. Their cancellation tokens cancel only the caller's wait; teardown continues in the background and remains observable through `RunAsync`.
- Startup/runtime/teardown failure is represented by `NantoHostException`, retaining the first failure and any later cleanup failures. The host still attempts to reach `Closed` before `RunAsync` throws.
- With `OnPrimaryWindowClosed` or `OnLastSurfaceClosed`, closing the single primary window requests application shutdown. With `Explicit`, the application message loop remains active with zero windows until `StopAsync` or cancellation.
- No new window or WebView operation may begin after its lifetime enters `Closing`.

### Asset provider contract

The lease returned by `PrepareAsync` must satisfy all of these conditions before the host accepts it:

- `RootDirectory` is an existing absolute path.
- `Version` is a non-empty safe directory component.
- `AssetPaths` is an ordinal set of normalized root-relative URL paths beginning with `/`, contains `/index.html`, and contains no dot segments, backslashes, duplicate decoded paths, or reserved `nanto-assets.json` entry.
- Every listed asset resolves beneath `RootDirectory` and exists as a regular non-reparse-point file.

Provide two built-in providers:

- `DirectoryWebAssetProvider`: validates and leases an existing build-output directory without copying it.
- `VersionedWebAssetProvider`: the production default, which materializes an embedded declared manifest into an atomic content-addressed cache using the Phase 0 hash-validation rules; a complete valid bundle is reused, while corrupt or incomplete bundles fail rather than being silently repaired.

The versioned provider uses this application-scoped schema:

```text
%LOCALAPPDATA%\Nanto\applications\<application-key>\
├── assets-v1\
│   ├── <bundle-sha256>\
│   │   ├── content\...
│   │   ├── manifest.json
│   │   ├── complete
│   │   ├── last-used.json
│   │   └── lease.lock
│   └── staging\
├── profiles\default\webview2-udf\
└── cache-maintenance.json
```

`<bundle-sha256>` covers the normalized manifest and every declared asset, not the application release version. Bundle content is immutable after atomic publication. Releases with identical frontend content reuse the same bundle, while different releases may keep different bundles concurrently. The stable UDF is application/profile-scoped rather than release-scoped so browser storage survives an upgrade.

Every prepared versioned lease holds a read handle to `lease.lock` with sharing that permits other readers but denies deletion. Multiple processes using the same bundle therefore coexist; the bundle remains protected until the last process releases its handle. The Windows window owns the lease and disposes it only after removing WebView mappings and closing the controller. Windows releases the handle after abnormal process termination.

A per-application cross-process maintenance lock serializes the brief lease-acquisition and cleanup decisions. After acquiring its lease, each successful `PrepareAsync` atomically records an explicit UTC timestamp in `last-used.json`. Best-effort cleanup runs at most once per day, never blocks successful startup on a deletion failure, and may retire a bundle only when all of these are true:

- it is not the bundle selected by the current preparation;
- its timestamp is valid, is not in the future, and is older than 30 days;
- no process holds its lease; and
- it is atomically renamed out of the active cache while the maintenance lock prevents a new lease acquisition.

Missing or invalid usage metadata is retained. Abandoned staging directories older than 24 hours may be removed under the same maintenance lock. An old executable can re-extract a retired embedded bundle on its next launch. Phase 1 fixes these safe defaults internally; public retention customization can be added later if real deployment evidence requires it.

The Windows host maps the lease through `https://app.nanto.invalid` with `DenyCors`. Exact files are served directly by WebView2. Only same-origin, top-level document routes without a file-like final segment may fall back to `/index.html`; queries and fragments remain unchanged. HTTP, file, data, JavaScript, encoded traversal, wrong-origin, and subresource fallback requests are rejected or left external according to the portable navigation policy established in Phase 0.

### Windows ownership and internal components

Use this ownership graph:

```text
WindowsApplicationHost
├── STA thread and COM apartment
├── WindowsUiDispatcher
├── application lifetime CancellationTokenSource
├── registered Win32 class
├── WebViewEnvironmentOwner
├── WindowRegistry
├── ResourceLedger
└── WindowsWindow (single primary window in Phase 1)
    ├── window lifetime CancellationTokenSource
    ├── owned HWND
    ├── IWebAssetLease
    ├── WebViewHost
    │   ├── controller and CoreWebView2
    │   ├── settings
    │   ├── event subscriptions and filters
    │   └── navigation/recovery state
    └── reverse-order CleanupStack
```

`WindowsApplicationHost` owns the UI thread, COM initialization, window class, shared environment, registry, and application cleanup. `WindowsWindow` owns its `HWND`, asset lease, controller, WebView, subscriptions, and window cancellation. Borrowers never release native resources.

`WindowRegistry` mutates only on the UI thread and publishes immutable array snapshots through `Volatile.Write`; readers never observe an in-progress mutation. Phase 1 rejects creation of a second window with `NotSupportedException`.

Every native/COM operation is wrapped at a narrow boundary that includes operation name and HRESULT or Win32 error in `NantoHostException`. Do not catch and ignore broad exceptions during teardown.

The default window presenter calls `ShowWindow` only when `WindowOptions.StartVisible` is true. TestApp selects an internal `SuppressedWindowPresenter` for a `Hidden` request through an `InternalsVisibleTo` test seam; it executes and records the presentation step but deliberately omits `ShowWindow`, activation, and foreground changes. It does not change window styles, controller type, bounds, navigation, or teardown. The hidden path therefore creates the same ordinary `WS_OVERLAPPEDWINDOW` and normal WebView2 controller as production.

### Renderer recovery

- Subscribe to `ProcessFailed` before initial navigation.
- For a renderer-process exit, raise `RendererFailed` and attempt exactly one `Reload` for that occurrence.
- Recovery succeeds only when navigation and the internal readiness signal complete within 15 seconds.
- If reload fails, times out, or immediately produces another renderer failure, transition the window to `Failed` and enter normal closing.
- Browser-process exit is not treated as renderer recovery; it fails the current environment/window, tears down fully, and reports the failure. Automatic whole-host recreation remains outside Phase 1 production behavior.

### Logging and resource ledger

Use source-generated `LoggerMessage` methods with stable numeric event IDs grouped by application lifecycle, window lifecycle, dispatcher, WebView, assets, and teardown. Log symbolic operation names, states, window IDs, HRESULTs, and elapsed durations. Never log web-message bodies, command payloads, cookies, local-storage values, or file contents.

The internal debug ledger tracks application hosts, UI threads, windows, native handles, COM objects, subscriptions, dispatcher items, asset leases, and browser processes. Acquisition and release are paired in the owning component. TestApp serializes initial, peak, and final snapshots. A successful or expected-failure scenario requires every owned count except the process's baseline OS handle count to return to zero.

### Failure-injection checkpoints

Define a `Phase1AcquisitionCheckpoint` enum shared internally by the host and IntegrationTestKit. The production default injector never fails. Tests inject immediately after the named acquisition succeeds.

Required checkpoints, in creation order:

1. `UiThreadStarted`
2. `ComInitialized`
3. `DispatcherCreated`
4. `WindowClassRegistered`
5. `WindowRegistered`
6. `HwndCreated`
7. `AssetLeasePrepared`
8. `WebViewEnvironmentCreated`
9. `BrowserProcessExitedSubscribed`
10. `ControllerCreated`
11. `CoreWebViewAcquired`
12. `SettingsAcquired`
13. `SettingsConfigured`
14. `NavigationStartingSubscribed`
15. `SourceChangedSubscribed`
16. `ContentLoadingSubscribed`
17. `ProcessFailedSubscribed`
18. `NavigationCompletedSubscribed`
19. `InternalReadinessSubscribed`
20. `WebResourceRequestedSubscribed`
21. `WebResourceFilterRegistered`
22. `VirtualHostMapped`
23. `InitialNavigationStarted`

The failure matrix runs one external process per checkpoint and asserts that all earlier acquisitions are released in exact reverse order. Checkpoints that do not apply to a scenario must be reported as not reached rather than silently passing.

### Integration harness protocol

TestApp accepts only:

```text
--request <absolute-json-path> --report <absolute-json-path>
```

The versioned JSON request contains scenario, presentation mode (`Hidden` or `Visible`), runtime lane (`Evergreen` only in Phase 1), optional failure checkpoint, iteration count, UDF, cache, and artifact paths. These values are supplied by the fixed integration project; they do not select tests inside that project.

The report contains protocol version, scenario, success/failure, host/runtime/architecture information, timestamps and durations, ordered checkpoints, lifecycle transitions, initial/peak/final ledger snapshots, renderer recovery result, and retained artifact paths.

IntegrationTestKit must:

- create a unique artifact, UDF, and cache directory per process;
- deliberately share only the cache directory in the multi-instance bundle-lease scenario while keeping its UDFs independent;
- launch TestApp without a shell and capture stdout/stderr asynchronously;
- assign TestApp and inherited Chromium children to a kill-on-close Job Object;
- use a 45-second default scenario timeout, 15-second renderer recovery timeout, 15-second shutdown timeout, and 10-second browser-exit timeout;
- kill the job on timeout and report it as failure;
- delete successful UDFs only after browser exit, while retaining failure artifacts;
- disable test parallelism in every real-WebView2 project.

Hidden and long-running projects always send `Hidden`; visible tests always send `Visible`. No integration project accepts a profile property, trait filter, or environment variable that changes its test inventory.

## Vertical milestones

1. **Repository and lifecycle foundation**
   - Establish the production-only root solution and record the external Phase 0 provenance.
   - Add production assemblies and fast test projects.
   - Implement state machines, shutdown policy, registry snapshots, cancellation-first shutdown, and cleanup aggregation.
   - Add fake host and dispatcher.

2. **Win32 host**
   - Add a dedicated STA thread, asynchronous dispatcher, message pump, primary `HWND`, and registry.
   - Support activation, focus, resize, close, destroy, and cancellation.
   - Number every native acquisition and test failure cleanup as each resource is introduced.

3. **WebView2 and assets**
   - Add shared environment and per-window controller ownership.
   - Add directory and versioned extracted asset providers.
   - Preserve secure virtual HTTPS, `DenyCors`, manifest-aware routing, SPA fallback, and origin validation.
   - Use source-generated COM with runtime marshalling disabled.

4. **DPI and monitor behavior**
   - Convert DIPs only at the Windows boundary.
   - Handle resize, DPI changes, minimum sizes, work areas, activation, focus, and negative monitor coordinates.
   - Keep cached DPI UI-thread-owned.

5. **Recovery and diagnostics**
   - Raise a portable renderer-failure event, attempt one reload, and close normally if recovery fails.
   - Complete subscription removal, controller closure, COM release, browser-exit synchronization, `HWND` destruction, and dispatcher shutdown.
   - Add structured logging and a development resource ledger without logging frontend payloads.

6. **Phase gate**
   - Execute hidden CoreCLR and Native AOT integration projects.
   - Confirm no UI-framework dependencies or unexplained trimming/AOT warnings.
   - Update README, `AGENTS.md`, and testing documentation with project-level commands.

## Test projects

### Default fast tests

`dotnet test` runs:

- Lifecycle transition matrices and invalid transitions.
- Concurrent/repeated stop and cancellation during initialization.
- Cleanup ordering, idempotence, continued cleanup after errors, and aggregation.
- Fake-host event ordering and shutdown behavior.
- Dispatcher affinity, queue ordering, and rejection during shutdown.
- Immutable registry snapshots.
- DIP conversion at common and fractional DPIs.
- Window-message decoding and monitor/work-area calculations.
- Asset manifest, cache, traversal, routing, and concurrent publication tests.
- Application-identity canonicalization, storage-key stability, bundle last-use boundaries, invalid/future timestamp retention, and cleanup eligibility.
- Win32/WebView2 ABI declarations.
- Dependency checks preventing platform types from entering portable APIs.

### Hidden WebView2 integration

```powershell
dotnet test tests/Nanto.Hosting.Windows.HiddenIntegrationTests
```

- Creates the normal production parent `HWND`.
- Never calls `ShowWindow` or activates the window.
- Uses the normal non-composition WebView2 controller.
- Runs scenarios in isolated child processes.
- Tests navigation, assets, routing, renderer recovery, close races, failure injection, browser exit, and zero-resource teardown.
- Starts two isolated processes against one bundle in the cache-lease scenario, proves cleanup cannot retire it until both leases close, and proves a terminated process releases its lease.

This is the CI-safe real-WebView2 project.

### Native AOT integration

```powershell
dotnet test tests/Nanto.Hosting.Windows.AotIntegrationTests
```

- Publishes the external hidden test host as `win-x64` Native AOT.
- Runs the critical hidden lifecycle scenarios.
- Fails on AOT/trimming warnings and unexpected dependencies.

### Visible desktop integration

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests
```

This project intentionally shows windows and covers only:

- Actual foreground activation and focus.
- Visible resizing and DWM behavior.
- Real cross-monitor `WM_DPICHANGED`.
- Initial monitor placement.
- Native input and screenshots.

It is never included in the root solution or ordinary CI. It runs only when someone directly invokes it on an isolated VM/session or intentionally accepts desktop interaction.

### Long-running integration tests

```powershell
dotnet test tests/Nanto.Hosting.Windows.LongRunningIntegrationTests
```

- Hidden windows only.
- Fixed documented soak counts.
- Repeated process isolation, lifecycle, and renderer recovery.
- Manual-only.

## CI and documentation policy

- Ordinary CI runs root `dotnet build` and `dotnet test`.
- Requested WebView2 CI runs the entire hidden integration project.
- Requested AOT CI runs the entire AOT integration project.
- Historical Phase 0 validation is performed only in Telaio and is not part of Nanto CI.
- Visible and long-running projects never run automatically.
- Test selection is expressed exclusively through project boundaries.
- Automatic GitHub Actions triggers remain disabled until a separate cost-policy decision enables them.

Hidden integration remains desktop-hosted even though no window is shown: it must run under an ordinary logged-on Windows user session capable of creating Chromium renderer/GPU processes. Do not run it as a Windows service or in a restrictive process sandbox.

## Milestone acceptance commands

Each milestone is committed only after its listed commands pass. These commands are cumulative.

### Milestone 1 — repository and lifecycle foundation

```powershell
dotnet build
dotnet test
```

Acceptance requires the root solution to contain no Phase 0 or integration-test project and all portable API dependency tests to pass. Nanto must build from a clean clone without Telaio present.

### Milestone 2 — Win32 host

```powershell
dotnet test
dotnet test tests/Nanto.Hosting.Windows.HiddenIntegrationTests
```

The hidden project initially covers `UiThreadStarted` through `HwndCreated`, dispatcher work, native close, cancellation, and ledger-zero shutdown. No top-level window may become visible or activated during the run.

### Milestone 3 — WebView2 and assets

```powershell
dotnet test
dotnet test tests/Nanto.Hosting.Windows.HiddenIntegrationTests
```

The complete 23-checkpoint failure matrix, secure-origin asset fixture, route fallback, navigation, browser-exit synchronization, and renderer-recovery scenarios must pass.

### Milestone 4 — DPI and monitor behavior

```powershell
dotnet test
dotnet test tests/Nanto.Hosting.Windows.HiddenIntegrationTests
```

All synthetic DPI/message tests must pass. The visible integration project is then run once on an isolated multi-monitor session and its environment/topology is recorded with the artifacts:

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests
```

### Milestone 5 — recovery and diagnostics

```powershell
dotnet test
dotnet test tests/Nanto.Hosting.Windows.HiddenIntegrationTests
dotnet test tests/Nanto.Hosting.Windows.AotIntegrationTests
```

All normal, injected-failure, close-race, timeout, and renderer-failure reports must end with a zero ledger. The Native AOT publish must produce no unexplained trim/AOT warning.

### Milestone 6 — Phase 1 gate

```powershell
dotnet build
dotnet test
dotnet test tests/Nanto.Hosting.Windows.HiddenIntegrationTests
dotnet test tests/Nanto.Hosting.Windows.AotIntegrationTests
```

Run `Nanto.Hosting.Windows.LongRunningIntegrationTests` separately only when explicitly approving its machine time. The Phase 1 gate report records the exact commands, SDK/runtime versions, architecture, results, known deferrals, and artifact locations.

## Completion criteria

- Root `dotnet test` never runs historical Phase 0 code or creates native windows.
- Phase 0 remains independently buildable and testable in the Telaio repository.
- Hidden integration tests display no windows and can run unattended.
- Visible behavior is isolated in an unmistakably named opt-in project.
- Every acquisition has fault-injection coverage.
- CoreCLR and Native AOT use identical production contracts and ownership paths.
- Success, failure, close races, and renderer recovery finish with a zero resource ledger.
- Every milestone has a passing commit with its cumulative acceptance commands recorded in the commit or gate evidence.
