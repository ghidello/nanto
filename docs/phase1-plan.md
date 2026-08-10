# Phase 1 — Windows Host and Lifecycle Kernel

## Summary

Phase 1 converts the Phase 0 evidence into production framework foundations using a clean rewrite. The evidence remains in the historical [Telaio `phase_1` branch](https://github.com/ghidello/telaio/tree/phase_1), sourced from commit [`0344107`](https://github.com/ghidello/telaio/commit/0344107c06ff36d2f89189bbb7660a46180193b1). Nanto production projects will not reference Phase 0 assemblies or require the Telaio repository to build.

Work will use small vertical commits and structurally separated test projects:

- `dotnet test` runs only production fast tests.
- Phase 0 remains independently reproducible from the Telaio repository.
- Hidden, AOT, visible, and long-running lanes are selected through explicit `Nanto.slnx` solution configurations.
- Phase 1 will not use test profiles, traits, filters, or environment variables to change a project's test inventory.

The integration strategy remains hybrid: pure tests for most behavior, hidden production-path WebView2 tests for unattended execution, and a small isolated visible-window project.

## Solution organization

### Root `Nanto.slnx`

This is the single canonical solution and contains every production, testing, support, and integration project. Its default `Debug` and `Release` configurations select only:

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

The solution defines these mutually exclusive test-lane configurations:

| Solution configuration | Selected projects and behavior |
| --- | --- |
| `Debug` / `Release` | Production libraries and the three fast test projects only. No TestApp or WebView2 process can start. |
| `Integration` | Production libraries, fast tests, TestProtocol, TestApp, IntegrationTestKit, and HiddenIntegrationTests. This is the unattended CoreCLR WebView2 lane. |
| `IntegrationAot` | Production libraries, fast tests, TestProtocol, TestApp, IntegrationTestKit, and AotIntegrationTests. Its test project publishes TestApp as Release Native AOT. |
| `IntegrationVisible` | Production libraries, fast tests, support projects, and VisibleIntegrationTests. It intentionally affects the interactive desktop. |
| `IntegrationLongRunning` | Production libraries, fast tests, support projects, and LongRunningIntegrationTests. It intentionally consumes extended machine time. |

The `Integration*` solution configurations map their selected SDK projects to the ordinary `Debug` project configuration; they are lane selectors, not alternative compiler optimization modes. The AOT project's publish target explicitly publishes TestApp with Release settings. No solution configuration selects more than one integration-test project, and there is deliberately no “all lanes” configuration.

Canonical selection therefore remains visible at the command line:

```powershell
dotnet test                       # fast tests only
dotnet test -c Integration        # fast + hidden WebView2
dotnet test -c IntegrationAot     # fast + Native AOT WebView2
dotnet test -c IntegrationVisible # explicit desktop interaction
dotnet test -c IntegrationLongRunning
```

### Historical Phase 0 boundary

Phase 0 source, tests, profiles, reports, and experiments stay in Telaio and are not copied into Nanto. Nanto records the decisions it adopts, but its build, tests, and release artifacts remain completely independent of the historical repository.

### Explicit Phase 1 integration projects

These projects are members of `Nanto.slnx`, remain visible in the IDE, and are selected only by their matching solution configuration:

- `Nanto.Hosting.Windows.HiddenIntegrationTests`
- `Nanto.Hosting.Windows.AotIntegrationTests`
- `Nanto.Hosting.Windows.VisibleIntegrationTests`
- `Nanto.Hosting.Windows.LongRunningIntegrationTests`

Their support projects—TestProtocol, TestApp, and IntegrationTestKit—also live under `tests/` and are solution members. Default configurations exclude all support and integration projects from build and test. Selecting an integration configuration includes only the required support graph and its one test project.

## Architecture and contracts

Create:

- `Nanto.Core`: portable lifecycle, application, window, dispatcher, asset, and failure contracts.
- `Nanto.Hosting.Windows`: STA host, Win32 window, WebView2, assets, navigation, DPI, logging, and teardown.
- `Nanto.Testing`: deterministic fake host/window, manual dispatcher, lifecycle recorder, and portable failure scripting.

Initial portable API:

- `ApplicationState`: `NotStarted`, `Creating`, `Created`, `Activated`, `Deactivated`, `Closing`, `Closed`, `Failed`.
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
| `src/Nanto.Testing/Nanto.Testing.csproj` | `net10.0` | References Core only; contains deterministic fakes, recording, and failure scripting without Windows or test-framework types. |
| `tests/Nanto.Core.Tests/Nanto.Core.Tests.csproj` | `net10.0` | References Core and the standard repository test packages. |
| `tests/Nanto.Hosting.Windows.Tests/Nanto.Hosting.Windows.Tests.csproj` | `net10.0-windows10.0.19041.0` | References Core and Windows hosting; contains no real window or WebView creation. |
| `tests/Nanto.Testing.Tests/Nanto.Testing.Tests.csproj` | `net10.0` | References Core and Testing. |
| `tests/Nanto.Hosting.Windows.TestProtocol/Nanto.Hosting.Windows.TestProtocol.csproj` | `net10.0` | Integration-only versioned request/report DTOs, scenario names, and serialized checkpoint names; references no production or test-framework assembly and is not a test project. |
| `tests/Nanto.Hosting.Windows.TestApp/Nanto.Hosting.Windows.TestApp.csproj` | `net10.0-windows10.0.19041.0` executable | References Core, Windows hosting, and TestProtocol; external process used by every native integration project; publishable as CoreCLR or Native AOT. |
| `tests/Nanto.Hosting.Windows.IntegrationTestKit/Nanto.Hosting.Windows.IntegrationTestKit.csproj` | `net10.0-windows10.0.19041.0` | References TestProtocol; contains the process runner, artifact handling, job-object containment, and integration assertions; it is not a test project. |
| Each `*IntegrationTests` project | `net10.0-windows10.0.19041.0` | References IntegrationTestKit and builds or publishes TestApp according to its single fixed execution model. |

Keep WebView2 and CsWin32 at the versions already pinned by Phase 0 until an explicit dependency change is reviewed. Add only [`Microsoft.Extensions.Logging.Abstractions` 10.0.10](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/10.0.10) to central package management. Do not add `Microsoft.Extensions.Hosting`, a DI container, OpenTelemetry SDK, or a UI framework in Phase 1.

The solution contains the complete project graph. Its `Debug` and `Release` configurations build the three production projects and three fast test projects only; each `Integration*` configuration adds its required support projects and exactly one integration-test project.

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

public readonly record struct WindowId
{
    public Guid Value { get; }

    public WindowId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A window ID cannot be empty.");
        }

        Value = value;
    }

    public static WindowId Create() => new(Guid.NewGuid());
}

public readonly record struct WindowBounds
{
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    public WindowBounds(double x, double y, double width, double height)
    {
        ValidateFinite(x, nameof(x));
        ValidateFinite(y, nameof(y));
        ValidateFinite(width, nameof(width));
        ValidateFinite(height, nameof(height));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Window coordinates and dimensions must be finite.");
        }
    }
}
```

`WindowBounds` uses DIPs. Construction and mutation reject non-finite values and non-positive width or height with `ArgumentOutOfRangeException`; negative X/Y values are valid for monitors left of or above the primary monitor. `WindowId.Create` must never return `Guid.Empty`. The CLR can still produce default values for both structs; `default(WindowId)` and `default(WindowBounds)` are invalid sentinels and every public boundary must reject them.

Use these option shapes:

```csharp
public sealed record WindowOptions
{
    public required string Title { get; init; }
    public WindowBounds InitialBounds { get; init; } = new(100, 100, 1024, 768);
    public bool StartVisible { get; init; } = true;
    public bool Resizable { get; init; } = true;
    public string InitialRoute { get; init; } = "/";
}

public sealed record NantoApplicationOptions
{
    public required string ApplicationId { get; init; }
    public required WindowOptions PrimaryWindow { get; init; }
    public required IWebAssetProvider Assets { get; init; }
    public ShutdownMode ShutdownMode { get; init; } = ShutdownMode.OnPrimaryWindowClosed;
    public ILoggerFactory? LoggerFactory { get; init; }
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
```

Option initializers remain data-only. `RunAsync` validates the complete immutable option graph before acquiring native resources: `Title` must contain a non-whitespace character; `InitialBounds` must not be the invalid default value; `InitialRoute` must be root-relative and reject absolute, scheme-relative, backslash-containing, dot-segment, or encoded-traversal paths; `ApplicationId`, `PrimaryWindow`, and `Assets` are required; and `ShutdownTimeout` must be positive and no greater than five minutes. A null `LoggerFactory` becomes `NullLoggerFactory.Instance` internally.

`ApplicationId` is canonicalized case-insensitively and must contain at least two dot-separated ASCII segments made from letters, digits, and interior hyphens. It is a persistent storage and security boundary, not a display label. Nanto derives `<application-key>` from the final readable segment plus the first 128 bits of SHA-256 over the canonical UTF-8 identifier, stores the complete identity in cache metadata, and refuses a metadata mismatch. Renaming an executable, assembly, or product display name does not change this identity; changing `ApplicationId` intentionally starts with a new cache and WebView2 profile.

Use these portable failure and event types:

```csharp
public enum NantoFailureStage
{
    Startup,
    Runtime,
    Teardown,
}

public enum RendererFailureKind
{
    Exited,
    Unresponsive,
    FrameRendererExited,
    Unknown,
}

public sealed class ApplicationStateChangedEventArgs : EventArgs
{
    public ApplicationState OldState { get; }
    public ApplicationState NewState { get; }
    public DateTimeOffset OccurredAt { get; }
    public Exception? Failure { get; }

    public ApplicationStateChangedEventArgs(ApplicationState oldState, ApplicationState newState, DateTimeOffset occurredAt, Exception? failure)
    {
        OldState = oldState;
        NewState = newState;
        OccurredAt = occurredAt;
        Failure = failure;
    }
}

public sealed class WindowStateChangedEventArgs : EventArgs
{
    public WindowState OldState { get; }
    public WindowState NewState { get; }
    public DateTimeOffset OccurredAt { get; }
    public Exception? Failure { get; }

    public WindowStateChangedEventArgs(WindowState oldState, WindowState newState, DateTimeOffset occurredAt, Exception? failure)
    {
        OldState = oldState;
        NewState = newState;
        OccurredAt = occurredAt;
        Failure = failure;
    }
}

public sealed class RendererFailedEventArgs : EventArgs
{
    public RendererFailureKind Kind { get; }
    public string Description { get; }
    public bool WillAttemptRecovery { get; }
    public DateTimeOffset OccurredAt { get; }

    public RendererFailedEventArgs(RendererFailureKind kind, string description, bool willAttemptRecovery, DateTimeOffset occurredAt)
    {
        Kind = kind;
        Description = description;
        WillAttemptRecovery = willAttemptRecovery;
        OccurredAt = occurredAt;
    }
}

public sealed class NantoHostException : Exception
{
    public NantoFailureStage Stage { get; }
    public string Operation { get; }
    public int? NativeErrorCode { get; }
    public IReadOnlyList<Exception> CleanupExceptions { get; }

    public NantoHostException(
        string message,
        NantoFailureStage stage,
        string operation,
        Exception primaryFailure,
        int? nativeErrorCode = null,
        IEnumerable<Exception>? cleanupExceptions = null)
        : base(message, primaryFailure)
    {
        Stage = stage;
        Operation = operation;
        NativeErrorCode = nativeErrorCode;
        CleanupExceptions = Array.AsReadOnly(cleanupExceptions?.ToArray() ?? Array.Empty<Exception>());
    }
}
```

All timestamps are UTC `DateTimeOffset` values. Portable lifecycle implementations receive a `TimeProvider` through an internal constructor seam; production uses `TimeProvider.System`, while `Nanto.Testing` supplies deterministic time. Public constructors validate non-empty descriptions and operation names, UTC-compatible timestamps, and non-null primary failures and copy all exception collections before exposing them.

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

`WindowsApplicationHost` is the public sealed implementation of `INantoApplicationHost` and has a public parameterless constructor. Construction stores no native resources and starts no thread; the dedicated UI thread and dispatcher are created by the first and only `RunAsync` call. Before that point, reading `Dispatcher` throws `InvalidOperationException`. After shutdown it returns the same dispatcher instance in its disposed state. `Windows` is always readable and returns an immutable snapshot: empty before startup, the current window during execution, and empty after teardown.

The built-in asset providers use these construction entry points:

- `DirectoryWebAssetProvider(string rootDirectory)` captures an absolute directory path and validates its lease contents during `PrepareAsync`.
- `VersionedWebAssetProvider.FromAssembly<TMarker>(string manifestResourceName)` uses `typeof(TMarker).Assembly` as an explicit AOT-safe resource owner and loads the named manifest resource without assembly scanning. The returned provider opens only the exact manifest and resource names declared by that manifest.

Neither provider acquires a lease or mutates the filesystem in its constructor or factory. `PrepareAsync` owns validation and acquisition, and the returned lease owns any filesystem handle it creates.

### Portable testing toolkit

`Nanto.Testing` is a public, test-framework-neutral package. It references `Nanto.Core` only and contains no xUnit, assertion-library, Windows, filesystem, real-thread, or wall-clock dependency. It provides:

- `ManualUiDispatcher`, which queues work until `RunNextAsync` or `DrainAsync` is called; while draining it temporarily installs its own synchronization context, callbacks observe dispatcher access, nested calls execute inline, posted continuations return to its queue, and the caller's prior context is restored afterward.
- `FakeNantoApplicationHost`, which uses the production portable lifecycle state machine and exposes deterministic gates for creation, activation, failure, stop, and close.
- `FakeNantoWindow`, which uses the production window state machine, records title/bounds/activation calls, and can raise a scripted renderer failure.
- `LifecycleRecorder`, which records immutable ordered application, window, and renderer events with timestamps supplied by a caller-provided `TimeProvider`.
- `FailurePlan`, which scripts failures at named portable operations such as application creation, window initialization, activation, and close. Windows acquisition checkpoints do not enter this package.

These utilities expose recorded calls and state; they do not provide assertion methods or throw test-framework-specific exceptions. `Nanto.Testing.Tests` tests the toolkit itself rather than duplicating Core or Windows-host tests.

`Nanto.Core` grants `InternalsVisibleTo` to `Nanto.Core.Tests` and `Nanto.Testing` so the fake host and window reuse the production portable state machines, cleanup aggregation, and internal `TimeProvider` seams instead of duplicating lifecycle logic. No Core or Testing friend access extends to the Windows host.

### Dispatcher semantics

- The Windows dispatcher is bound to the dedicated STA thread and uses a private `WM_APP` message to drain a FIFO queue.
- The UI thread installs a private `SynchronizationContext` backed by that queue before any user or WebView callback can run.
- `CheckAccess` compares the current managed thread ID with the owning UI thread ID.
- Calls made on the UI thread execute inline. Calls from other threads enqueue work and return an incomplete `ValueTask`.
- An ordinary `await` inside a dispatcher callback intentionally captures the private context, so its continuation returns to the UI thread. Framework code must not use `ConfigureAwait(false)` when the continuation touches Win32, WebView2, window state, or another UI-owned resource; `ConfigureAwait(true)` is redundant and is normally omitted.
- Thread-agnostic lower-level operations should use `ConfigureAwait(false)` when appropriate and must re-enter through `IUiDispatcher` before accessing UI-owned state. A callback that deliberately suppresses context capture is responsible for explicitly dispatching its UI continuation.
- Cancellation before execution removes or skips the queued callback and completes it with `OperationCanceledException`. Once execution begins, cancellation is only passed to asynchronous callbacks and cannot interrupt synchronous work.
- Callback exceptions are propagated to the returned task. Continuations are completed asynchronously so they cannot re-enter the native callback stack.
- Once application state reaches `Closing`, new dispatcher work fails with `ObjectDisposedException`; queued work that has not started is canceled with the application lifetime token, and work already running receives that token. Every delegate is invoked at most once.
- Null delegates throw `ArgumentNullException` synchronously. The dispatcher remains queryable after shutdown but rejects all invocation operations.
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
- `RunAsync` after `DisposeAsync` throws `ObjectDisposedException`. An already-canceled run token still starts the UI lifecycle, transitions from `Creating` to `Closing`, completes deterministic cleanup, and reaches `Closed` without creating a window or WebView.
- Cancellation of the `RunAsync` token requests orderly shutdown; it does not abandon cleanup. `RunAsync` completes normally if shutdown succeeds.
- `StopAsync` before `RunAsync` is a completed no-op. Once `RunAsync` has atomically claimed the host, `StopAsync` and `CloseAsync` are idempotent and safe concurrently. Their cancellation tokens cancel only the caller's wait; teardown continues in the background and remains observable through `RunAsync`. `StopAsync` after `Closed` completes immediately.
- `DisposeAsync` before `RunAsync` marks the host disposed without starting a UI thread or raising lifecycle events; `State` remains `NotStarted`. During execution it requests stop and waits without cancellation until teardown reaches `Closed`. After closure and on repeated or concurrent calls it completes immediately.
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
- `VersionedWebAssetProvider`: the production default, which materializes an embedded declared manifest into the atomic content-addressed cache specified below; a complete valid bundle is reused, while corrupt or incomplete bundles fail rather than being silently repaired.

#### Manifest, path, and bundle normalization

The embedded `nanto-assets.json` manifest has schema version 1 and declares each asset's normalized URL path, exact assembly resource name, byte length, and uppercase 64-character SHA-256. Manifest deserialization uses a source-generated JSON context and rejects unknown schema versions, missing members, duplicate JSON properties, negative lengths, malformed hashes, duplicate resource names, and resources not found in the explicitly selected assembly.

Manifest URL paths are decoded Unicode paths, not URI-escaped strings. Normalize each path to Unicode Form C and require exactly one leading `/`. Reject a trailing slash, empty segments, `.`, `..`, backslashes, percent signs, colons, control characters, query or fragment delimiters, and the reserved `/nanto-assets.json` path. Require `/index.html`. Reject collisions under both `StringComparer.Ordinal` and `StringComparer.OrdinalIgnoreCase` so the manifest has one meaning on the Windows filesystem while URL lookup remains ordinal and case-sensitive.

Compute each file SHA-256 while materializing it and verify its declared length and hash before publication. Compute `<bundle-sha256>` as SHA-256 over this byte sequence:

```text
UTF8("NANTO-ASSETS-V1") || 0x00 ||
for each entry ordered by normalized URL path using ordinal comparison:
    UTF8(path) || 0x00 || Int64BigEndian(length) || Raw32ByteFileSha256
```

The hash therefore depends on normalized public paths and content, not JSON formatting, manifest property order, assembly resource names, or application release version. Persisted `manifest.json` records schema version, canonical application identity, bundle hash, and the sorted path/length/hash entries. `complete` contains the bundle hash followed by a newline.

Publication writes only beneath a unique `staging` child, rejects reparse points in every existing path component, flushes and closes all content and metadata, writes `complete` last, and atomically renames the staging directory to `<bundle-sha256>`. A concurrent winner is reusable only after full validation. Validation requires the expected application identity and bundle hash, the exact declared file set with no additional content files, matching lengths and hashes, a matching completion marker, regular non-reparse-point files, and paths that remain beneath the bundle root. Any existing incomplete, corrupt, or identity-mismatched destination fails startup with `InvalidDataException`; startup never edits or silently repairs it.

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

#### Request and navigation normalization

The Windows host maps the lease through `https://app.nanto.invalid` with `DenyCors`. URI parsing must succeed as an absolute URI and user information is always rejected. Scheme and host comparison is ordinal-ignore-case; the effective port must match. Queries and fragments do not participate in asset lookup and remain unchanged on an SPA fallback navigation.

For the application origin, decode each path segment exactly once as UTF-8, normalize it to Unicode Form C, and then apply the manifest path rules. Reject malformed escapes, encoded `/` or `\`, encoded or decoded dot segments, a remaining percent-encoded traversal token after the first decode, control characters, and any path whose decoded and normalized form is ambiguous. Exact ordinal matches in `AssetPaths` are served by WebView2's virtual-host mapping.

A same-origin request falls back to `/index.html` only when it is a top-level document navigation and the final normalized path segment contains no `.`. Subresources and file-like routes never fall back. Wrong-origin HTTP or HTTPS navigation is left to WebView2 and receives no Nanto asset response or future local-origin capability; wrong-origin subresources are likewise left to WebView2 and its CORS policy. `file:`, `data:`, `javascript:`, malformed, credential-bearing, and other schemes are canceled.

Golden decisions:

| Candidate | Context | Decision |
| --- | --- | --- |
| `https://app.nanto.invalid/assets/app.js?v=1` | Any | Serve exact `/assets/app.js`. |
| `https://app.nanto.invalid/settings/profile#name` | Top-level document | Fall back to `/index.html`, preserving query/fragment. |
| `https://app.nanto.invalid/settings/profile.json` | Top-level document | Reject fallback because the final segment is file-like. |
| `https://app.nanto.invalid/missing` | Subresource | Reject fallback. |
| `https://app.nanto.invalid/%2e%2e/secret` | Any | Reject encoded traversal. |
| `https://example.com/` | Top-level document | Leave to WebView2 as external content with no local-origin capability. |
| `file:///c:/secret` or `javascript:alert(1)` | Any | Cancel. |

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

Define `Phase1AcquisitionCheckpoint` and the failure-injector interface as internal members of `Nanto.Hosting.Windows`. The production default injector never fails. Grant `InternalsVisibleTo` only to TestApp so it can install an injector and map TestProtocol's serialized checkpoint name to the internal enum; IntegrationTestKit never references the production assembly. Unknown or incorrectly cased checkpoint names are rejected before the host starts. Tests inject immediately after the named acquisition succeeds.

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

TestProtocol owns the source-generated JSON context and versioned DTOs used by both TestApp and IntegrationTestKit. The JSON request contains scenario, presentation mode (`Hidden` or `Visible`), runtime lane (`Evergreen` only in Phase 1), optional case-sensitive failure-checkpoint name, iteration count, UDF, cache, and artifact paths. These values are supplied by the fixed integration project; they do not select tests inside that project.

The report contains protocol version, scenario, success/failure, host/runtime/architecture information, timestamps and durations, ordered serialized checkpoint names, lifecycle transitions, initial/peak/final ledger snapshots, renderer recovery result, and retained artifact paths. Protocol DTOs expose no internal production enum or exception type.

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

`dotnet test` runs the three fast projects with non-overlapping ownership.

`Nanto.Core.Tests` owns:

- `WindowId`, `WindowBounds`, options, route, and default-value validation.
- Application/window transition matrices, invalid transitions, shutdown modes, single-use run, stop/disposal edge cases, cancellation during initialization, event ordering, timestamping, and failure aggregation.
- Cleanup ordering, idempotence, continued cleanup after errors, and aggregation.
- Application-identity canonicalization and storage-key stability.
- Portable manifest-path normalization and the complete navigation golden-decision table.
- Dependency checks preventing platform types from entering portable APIs.

`Nanto.Hosting.Windows.Tests` owns:

- Windows dispatcher affinity, synchronization-context capture, FIFO ordering, cancellation, exception propagation, and rejection during shutdown.
- Immutable registry snapshots and second-window rejection.
- DIP conversion at common and fractional DPIs, window-message decoding, and monitor/work-area calculations.
- Embedded-manifest parsing, resource validation, bundle hashing, materialization, exact-file validation, traversal/reparse-point rejection, and concurrent publication.
- Cache lease and maintenance behavior: last-use boundaries, invalid/future timestamp retention, cleanup eligibility, and application-identity mismatch.
- Win32/WebView2 ABI declarations and dependency checks preventing UI-framework packages from entering the host.

`Nanto.Testing.Tests` owns:

- `ManualUiDispatcher` explicit draining, nested inline execution, synchronization-context capture/restoration, FIFO order, cancellation, exception propagation, shutdown, and exactly-once invocation.
- `FakeNantoApplicationHost` single-use run, deterministic gates, repeated/concurrent stop, caller-wait cancellation, disposal, failure aggregation, and application event ordering.
- `FakeNantoWindow` recorded mutations, valid transitions, idempotent close, and scripted renderer-failure events.
- `LifecycleRecorder` immutable snapshots, ordering, caller-provided time, and isolation between runs.
- `FailurePlan` ordered matching, deterministic triggering, exhaustion, and diagnostics for unexpected portable operations.
- A dependency test proving the package contains no Windows or test-framework reference and uses no real thread, filesystem, or wall-clock timing.

### Hidden WebView2 integration

```powershell
dotnet test -c Integration
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
dotnet test -c IntegrationAot
```

- Publishes the external hidden test host as `win-x64` Native AOT.
- Runs the critical hidden lifecycle scenarios.
- Fails on AOT/trimming warnings and unexpected dependencies.

### Visible desktop integration

```powershell
dotnet test -c IntegrationVisible
```

This project intentionally shows windows and covers only:

- Actual foreground activation and focus.
- Visible resizing and DWM behavior.
- Real cross-monitor `WM_DPICHANGED`.
- Initial monitor placement.
- Native input and screenshots.

It is excluded from the default solution configurations and ordinary CI. It runs only when someone explicitly selects `IntegrationVisible` on an isolated VM/session or intentionally accepts desktop interaction.

### Long-running integration tests

```powershell
dotnet test -c IntegrationLongRunning
```

- Hidden windows only.
- Fixed documented soak counts.
- Repeated process isolation, lifecycle, and renderer recovery.
- Manual-only.

## CI and documentation policy

- Ordinary CI runs root `dotnet build` and `dotnet test` using the default configuration.
- Requested WebView2 CI runs `dotnet test -c Integration`.
- Requested AOT CI runs `dotnet test -c IntegrationAot`.
- Historical Phase 0 validation is performed only in Telaio and is not part of Nanto CI.
- `IntegrationVisible` and `IntegrationLongRunning` never run automatically.
- Test-lane selection is expressed exclusively through solution configurations and fixed project boundaries; selecting a configuration never changes the tests compiled into a project.
- Automatic GitHub Actions triggers remain disabled until a separate cost-policy decision enables them.

Hidden integration remains desktop-hosted even though no window is shown: it must run under an ordinary logged-on Windows user session capable of creating Chromium renderer/GPU processes. Do not run it as a Windows service or in a restrictive process sandbox.

## Milestone acceptance commands

Each milestone is committed only after its listed commands pass. These commands are cumulative.

### Milestone 1 — repository and lifecycle foundation

```powershell
dotnet build
dotnet test
```

Acceptance requires the default solution configurations to exclude Phase 0, support, and integration-test projects and all portable API dependency tests to pass. Nanto must build from a clean clone without Telaio present.

### Milestone 2 — Win32 host

```powershell
dotnet test -c Integration
```

The hidden project initially covers `UiThreadStarted` through `HwndCreated`, dispatcher work, native close, cancellation, and ledger-zero shutdown. No top-level window may become visible or activated during the run.

### Milestone 3 — WebView2 and assets

```powershell
dotnet test -c Integration
```

The complete 23-checkpoint failure matrix, secure-origin asset fixture, route fallback, navigation, browser-exit synchronization, and renderer-recovery scenarios must pass.

### Milestone 4 — DPI and monitor behavior

```powershell
dotnet test -c Integration
```

All synthetic DPI/message tests must pass. The visible integration project is then run once on an isolated multi-monitor session and its environment/topology is recorded with the artifacts:

```powershell
dotnet test -c IntegrationVisible
```

### Milestone 5 — recovery and diagnostics

```powershell
dotnet test -c Integration
dotnet test -c IntegrationAot
```

All normal, injected-failure, close-race, timeout, and renderer-failure reports must end with a zero ledger. The Native AOT publish must produce no unexplained trim/AOT warning.

### Milestone 6 — Phase 1 gate

```powershell
dotnet build
dotnet test
dotnet test -c Integration
dotnet test -c IntegrationAot
```

Run `dotnet test -c IntegrationLongRunning` separately only when explicitly approving its machine time. The Phase 1 gate report records the exact commands, SDK/runtime versions, architecture, results, known deferrals, and artifact locations.

## Completion criteria

- Root `dotnet test` in the default configuration never runs historical Phase 0 code or creates native windows.
- Phase 0 remains independently buildable and testable in the Telaio repository.
- Hidden integration tests display no windows and can run unattended.
- Visible behavior is isolated in an unmistakably named opt-in project.
- Every acquisition has fault-injection coverage.
- CoreCLR and Native AOT use identical production contracts and ownership paths.
- Success, failure, close races, and renderer recovery finish with a zero resource ledger.
- Every milestone has a passing commit with its cumulative acceptance commands recorded in the commit or gate evidence.
