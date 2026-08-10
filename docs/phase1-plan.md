# Phase 1 — Windows Host and Lifecycle Kernel

## Summary

Phase 1 creates Nanto's production framework foundations as a clean implementation. The feasibility work completed before the rename established that the chosen raw-Win32/WebView2 and Native AOT direction is viable; every requirement adopted from that work is stated explicitly in this document. Nanto does not require another repository, assembly, report, test profile, or experiment to build or to interpret this plan.

Work will use small vertical commits and structurally separated test projects:

- `dotnet test` runs only production fast tests.
- Hidden framework-dependent CoreCLR, self-contained CoreCLR, Native AOT, interop-generation, visible, and long-running lanes are selected through explicit `Nanto.slnx` solution configurations.
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
| `IntegrationSelfContained` | Production libraries, fast tests, TestProtocol, TestApp, IntegrationTestKit, and CoreClrSelfContainedIntegrationTests. This is the unattended self-contained CoreCLR deployment lane. |
| `IntegrationAot` | Production libraries, fast tests, TestProtocol, TestApp, IntegrationTestKit, and AotIntegrationTests. Its test project publishes TestApp as Release Native AOT. |
| `IntegrationInterop` | Production libraries, fast tests, WebView2InteropGen, and InteropGeneration.IntegrationTests. It verifies committed interop without creating a window or WebView2 process. |
| `IntegrationVisible` | Production libraries, fast tests, support projects, and VisibleIntegrationTests. It intentionally affects the interactive desktop. |
| `IntegrationLongRunning` | Production libraries, fast tests, support projects, and LongRunningIntegrationTests. It intentionally consumes extended machine time. |

The `Integration*` solution configurations map their selected SDK projects to the ordinary `Debug` project configuration; they are lane selectors, not alternative compiler optimization modes. The AOT project's publish target explicitly publishes TestApp with Release settings. No solution configuration selects more than one integration-test project, and there is deliberately no “all lanes” configuration.

Canonical selection therefore remains visible at the command line:

```powershell
dotnet test                       # fast tests only
dotnet test -c Integration        # fast + hidden WebView2
dotnet test -c IntegrationSelfContained
dotnet test -c IntegrationAot     # fast + Native AOT WebView2
dotnet test -c IntegrationInterop # deterministic interop verification
dotnet test -c IntegrationVisible # explicit desktop interaction
dotnet test -c IntegrationLongRunning
```

### Explicit Phase 1 integration projects

These projects are members of `Nanto.slnx`, remain visible in the IDE, and are selected only by their matching solution configuration:

- `Nanto.Hosting.Windows.HiddenIntegrationTests`
- `Nanto.Hosting.Windows.CoreClrSelfContainedIntegrationTests`
- `Nanto.Hosting.Windows.AotIntegrationTests`
- `Nanto.Hosting.Windows.InteropGeneration.IntegrationTests`
- `Nanto.Hosting.Windows.VisibleIntegrationTests`
- `Nanto.Hosting.Windows.LongRunningIntegrationTests`

Their support projects—TestProtocol, TestApp, IntegrationTestKit, and the offline WebView2InteropGen tool—are also solution members. Default configurations exclude all support, tooling, and integration projects from build and test. Selecting an integration configuration includes only the required support graph and its one test project.

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
| `eng/Nanto.WebView2InteropGen/Nanto.WebView2InteropGen.csproj` | `net10.0` executable | Offline deterministic generator for the narrow WebView2 COM projection; consumes official inputs from the pinned SDK package and has no production runtime role. |
| `tests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests.csproj` | `net10.0` | References the generator, regenerates into its own `obj` tree, and compares committed outputs byte-for-byte without creating a window or WebView2 process. |
| Each real-WebView2 `*IntegrationTests` project | `net10.0-windows10.0.19041.0` | References IntegrationTestKit and builds or publishes TestApp according to its single fixed execution model. The portable interop-generation project is the deliberate exception. |

Pin `Microsoft.Web.WebView2` at `1.0.4129.50`, `Microsoft.Windows.CsWin32` at `0.3.298`, and `Microsoft.Extensions.Logging.Abstractions` at `10.0.10` in central package management. Upgrade WebView2 and CsWin32 only through an explicit dependency review that includes interop regeneration, ABI verification, AOT/trim diagnostics, loader verification, and real-host integration tests. Do not add `Microsoft.Extensions.Hosting`, a DI container, OpenTelemetry SDK, or a UI framework in Phase 1.

The solution contains the complete project graph. Its `Debug` and `Release` configurations build the three production projects and three fast test projects only; each `Integration*` configuration adds its required support or tooling projects and exactly one integration-test project.

TestApp declares the `win-x64` runtime identifier and supports exactly three explicit build modes: `CoreClrFrameworkDependent`, `CoreClrSelfContained`, and `NativeAot`. Ordinary builds default to `CoreClrFrameworkDependent`; production tooling defaults to `NativeAot`. Selecting CoreCLR is deliberate and never occurs as a silent fallback after an AOT failure. Arm64 is outside Phase 1 and requires a future platform gate rather than conditional code in the initial host.

The mode property is `NantoBuildMode`. It is validated before compilation and selects isolated project-local trees:

```text
obj/CoreClrFrameworkDependent/
obj/CoreClrSelfContained/
obj/NativeAot/
bin/CoreClrFrameworkDependent/
bin/CoreClrSelfContained/
bin/NativeAot/
```

`DefaultItemExcludes` excludes every project-local `obj/**` and `bin/**` tree so generated sources from a sibling mode cannot be compiled accidentally after moving `BaseIntermediateOutputPath` and `BaseOutputPath`. An unknown mode fails with the accepted values named. This isolation prevents an AOT publish from reusing a managed assembly compiled under CoreCLR settings, or the inverse.

Mode behavior is fixed:

| `NantoBuildMode` | Publish properties | Host form | WebView2 loader |
| --- | --- | --- | --- |
| `CoreClrFrameworkDependent` | `PublishAot=false`, `SelfContained=false`, `UseAppHost=false`, no trimming | DLL launched by an installed compatible `dotnet` host | Adjacent Microsoft-signed `WebView2Loader.dll` |
| `CoreClrSelfContained` | `PublishAot=false`, `SelfContained=true`, `UseAppHost=true`, no trimming | Self-contained x64 executable and runtime files | Adjacent Microsoft-signed `WebView2Loader.dll` |
| `NativeAot` | `PublishAot=true`, `SelfContained=true`, `UseAppHost=true`, full trimming and complete AOT analysis | Native x64 executable | Statically linked `WebView2LoaderStatic.lib`; no loader DLL |

All modes compile the same production projects, generated COM projection, lifecycle, security, serialization, asset, and teardown code. CoreCLR is a compatibility/development deployment choice, not permission to introduce framework code that fails AOT analysis. Nanto production assemblies remain `IsAotCompatible` and the Native AOT lane stays green.

The `Integration` solution configuration runs framework-dependent CoreCLR TestApp. `IntegrationSelfContained` publishes once to `artifacts/phase1/publish/coreclr-self-contained/win-x64`; `IntegrationAot` publishes once to `artifacts/phase1/publish/native-aot/win-x64`. Each integration project fixes `NantoBuildMode` in MSBuild; no trait, environment variable, profile, or test argument changes its build mode or inventory.

Every production publish sets `CopyOutputSymbolsToPublishDirectory=false` while leaving symbol generation enabled. The integration build copies the resulting managed or native PDB from its mode-specific tree into `artifacts/phase1/symbols/<mode>/win-x64`, records size and SHA-256, and rejects PDBs in the deployable publish directory. If Native AOT places a linker PDB in publish output, the publish target moves it to the symbol artifact before validating deployment; it never deletes the only diagnostic copy or disables symbols.

### WebView2 interop generation and verification

Phase 1 uses an offline repository tool, not a Roslyn generator that runs in every application build. The generated projection is committed, reviewed, and compiled as ordinary internal source in `Nanto.Hosting.Windows`. Production builds therefore do not require a C parser, do not write into the source tree, and do not acquire a generator dependency. Regeneration occurs only when intentionally updating the WebView2 SDK, the selected projection surface, or the generator format.

Use this repository structure:

```text
eng/
└── Nanto.WebView2InteropGen/
    ├── Nanto.WebView2InteropGen.csproj
    ├── Generator.cs
    ├── Program.cs
    └── webview2-interop-spec.json

src/Nanto.Hosting.Windows/
└── Interop/Generated/
    ├── WebView2Interop.g.cs
    └── webview2-interop-manifest.json

tests/
└── Nanto.Hosting.Windows.InteropGeneration.IntegrationTests/
    ├── Nanto.Hosting.Windows.InteropGeneration.IntegrationTests.csproj
    └── InteropGenerationTests.cs
```

The generator and its integration test are members of `Nanto.slnx` but excluded from `Debug` and `Release`. Ordinary `dotnet build` and `dotnet test` consume only committed `WebView2Interop.g.cs`; `dotnet test -c IntegrationInterop` selects deterministic regeneration and comparison. The verification lane is safe for unattended CI because it creates no native window, browser process, or desktop interaction.

#### Authoritative inputs

`Microsoft.Web.WebView2` remains centrally pinned. The generator project references it with `GeneratePathProperty=true`, `PrivateAssets=all`, and `ExcludeAssets=all`. This restores the package and exposes its path to MSBuild without copying managed WebView2 assemblies or `WebView2Loader.dll` into the generator output.

The tool consumes these files directly from the restored NuGet package:

```text
<package-root>/WebView2.idl
<package-root>/build/native/include/WebView2.h
```

No WebView2 header, IDL, SDK extraction, or copied package tree belongs under repository `artifacts/`. `artifacts/` remains reserved for publish outputs, integration reports, screenshots, dumps, and performance evidence. The NuGet global-packages directory is an input cache, while generated verification output belongs under the integration project's ignored `obj` directory.

`webview2-interop-spec.json` is the reviewed allowlist. It names every interface, callback interface, enum, structure, loader function, and method required by the production host. The generator computes the transitive base-interface method closure so the emitted vtable order is complete, but it must not project unrelated SDK surface. Adding a WebView2 API requires changing this specification, reviewing the generated diff, and extending ABI or behavior tests.

The specification contains symbolic names and selection policy, not copied GUIDs, native signatures, vtable slots, or numeric enum values. Those facts must come from the official inputs so that verification can detect SDK drift. Its versioned top-level shape is:

```json
{
  "schemaVersion": 1,
  "namespace": "Nanto.Hosting.Windows.Interop",
  "interfaces": [
    {
      "name": "ICoreWebView2",
      "methods": ["add_WebMessageReceived", "remove_WebMessageReceived", "Navigate"]
    }
  ],
  "types": ["EventRegistrationToken", "COREWEBVIEW2_PROCESS_FAILED_KIND"],
  "loaderFunctions": [
    "CreateCoreWebView2EnvironmentWithOptions",
    "GetAvailableCoreWebView2BrowserVersionString"
  ]
}
```

The real specification expands this list to the complete production surface. Interface order in the JSON is not ABI order; the generator resolves inheritance and native declaration order from the SDK, emits a canonical order, and fails if a named method or type is absent or ambiguous. Selecting an interface without listing methods does not mean “generate everything”; an explicit `allMethods` escape hatch is forbidden in Phase 1.

The IDL supplies interface identities, inheritance, method declarations, enums, and callback shapes. The generated native header independently supplies the concrete ABI spelling and method order used for validation. The parser is deliberately narrow and fail-closed: an unknown selected construct, ambiguous type mapping, changed inheritance chain, or disagreement between IDL and header terminates generation with the interface and native declaration named in the diagnostic. Phase 1 does not add Clang, Windows App SDK, WinUI, or a WebView2 managed projection dependency merely to generate this surface.

#### Generated source rules

`WebView2Interop.g.cs` contains only the internal namespace `Nanto.Hosting.Windows.Interop` and the selected native surface. It uses .NET source-generated COM attributes and ABI-safe types compatible with `DisableRuntimeMarshalling=true`; it must not use `ComImport`, runtime-created RCWs/CCWs, `dynamic`, reflection-based activation, or hand-copied host-side vtable calls. Generated callback interfaces, GUIDs, inheritance, string marshalling, event tokens, enums, and architecture-sensitive structures remain covered by host ABI tests.

Generation is deterministic:

- UTF-8 without BOM and LF line endings on every operating system;
- invariant-culture formatting and ordinal ordering;
- no timestamps, absolute paths, NuGet-cache paths, machine names, user names, or random values;
- a stable banner naming the update and verification commands;
- the same inputs and generator format produce identical bytes.

Because verification is byte-for-byte and Windows Git clients commonly enable newline conversion, Phase 1 adds explicit `.gitattributes` entries with `text eol=lf` for the projection specification and both committed generated files. The generator always writes one terminal newline. JSON uses two-space indentation, property order defined by the manifest schema, and no insignificant trailing whitespace.

The generated loader declarations use one logical library name for both execution modes. CoreCLR copies the pinned package's Microsoft-signed `runtimes/win-x64/native/WebView2Loader.dll` beside the host. Native AOT declares that library as a direct P/Invoke and passes the pinned package's `build/native/x64/WebView2LoaderStatic.lib` to the native linker. Publish validation fails if CoreCLR lacks its loader DLL or Native AOT contains one or imports it dynamically. The Evergreen Runtime remains external in both cases.

The production project includes `WebView2Interop.g.cs` as normal compiled source and `webview2-interop-manifest.json` as a non-runtime repository artifact. Generated files are never edited manually. Small handwritten adapters may wrap the projection to express ownership, HRESULT translation, or host policy, but must not duplicate generated native declarations.

#### Manifest contract

`webview2-interop-manifest.json` is deterministic JSON with a versioned schema. It records at least:

- manifest schema version and generator format version;
- WebView2 package ID and exact pinned version;
- package-relative IDL and header paths plus SHA-256 for each input;
- repository-relative projection-spec path and its SHA-256;
- ordered selected interface/type names, IIDs, base interfaces, and emitted method counts;
- generated C# relative path, byte length, SHA-256, encoding, and line-ending policy.

The manifest does not hash itself and contains no absolute path or generation time. A package upgrade that changes either official input therefore produces a reviewable manifest diff even when the selected C# surface happens not to change.

#### Update command

The generator project defines an explicit `UpdateWebView2Interop` MSBuild target that depends on its normal build. MSBuild passes the resolved package root, repository projection specification, and committed output directory to `Program.cs`; developers never need to discover or type a NuGet-cache path.

The generator project uses these MSBuild property names as its internal command contract:

| Property | Value |
| --- | --- |
| `WebView2InteropPackageRoot` | `$(PkgMicrosoft_Web_WebView2)` from the pinned package reference |
| `WebView2InteropSpecPath` | `$(MSBuildProjectDirectory)\webview2-interop-spec.json` |
| `WebView2InteropUpdateDirectory` | `$(MSBuildProjectDirectory)\obj\update\$(Configuration)\Generated` |
| `WebView2InteropCommittedDirectory` | repository `src\Nanto.Hosting.Windows\Interop\Generated` directory |

All paths passed to the executable are normalized absolute paths. The target quotes each argument, creates only the ignored update directory, and validates that the committed destination resolves beneath `src/Nanto.Hosting.Windows/Interop/Generated` before replacing files.

```powershell
dotnet build eng/Nanto.WebView2InteropGen/Nanto.WebView2InteropGen.csproj `
  -c Release `
  -t:UpdateWebView2Interop
```

The target first generates both files into `eng/Nanto.WebView2InteropGen/obj/update/<Configuration>/Generated/`, validates them, and only then replaces changed committed outputs. Failure before replacement leaves the last committed projection intact. A successful update prints the package version, input hashes, selected surface count, changed files, and the verification command. The default `Build` target never updates committed files.

`Program.cs` is a thin argument/exit-code boundary over `Generator.cs`. `Generator.cs` accepts explicit input and output paths and returns structured diagnostics so the update target and integration tests exercise the same implementation. CLI errors use a nonzero exit code and identify missing inputs, unsupported syntax, manifest mismatch, or write failure without dumping the full SDK header.

#### Verification integration test

The canonical verification command is:

```powershell
dotnet test -c IntegrationInterop
```

The test project references the generator and the pinned WebView2 package for build-time inputs only. Its project file evaluates three absolute paths and emits them as test-assembly metadata: the repository root, the restored WebView2 package root, and the verification output directory. `InteropGenerationTests.cs` reads that metadata; it never guesses the checkout depth, walks the NuGet cache, or reconstructs an `obj` path from `AppContext.BaseDirectory`.

Use these exact assembly-metadata keys:

| Key | MSBuild value |
| --- | --- |
| `Nanto.RepositoryRoot` | normalized repository root derived from the test project location at build time |
| `Nanto.WebView2PackageRoot` | `$(PkgMicrosoft_Web_WebView2)` |
| `Nanto.InteropVerificationDirectory` | normalized `$(MSBuildProjectDirectory)\obj\interop-verification\$(Configuration)` |

The test project emits them through SDK `AssemblyMetadata` items. Absolute machine-specific values are acceptable in this non-shipping test assembly; they never enter generated source, the manifest, production binaries, or committed files. A missing, duplicate, relative, or empty metadata value fails before generation with the metadata key named.

The verification directory is:

```text
tests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests/
  obj/interop-verification/<Configuration>/Generated/
```

The test validates that this resolved directory remains beneath its own project `obj` root, clears only that exact verification directory, invokes `Generator.cs` in-process, and leaves the generated files there for inspection after the run. Keeping the files under `obj` makes normal `dotnet clean` semantics correct and avoids persistent repository noise; assembly metadata makes the location straightforward for the test to consume.

Verification then:

1. Generates `WebView2Interop.g.cs` and `webview2-interop-manifest.json` from the restored pinned package and committed specification.
2. Generates a second time into `obj/interop-verification/<Configuration>/Determinism/Generated` and proves the two results are byte-identical.
3. Compares each regenerated file with its committed counterpart using `File.ReadAllBytes`, not normalized text comparison.
4. Recomputes every manifest input and output hash and checks the selected interface closure against the specification.
5. Checks that the output contains the required source-generated COM model and none of the forbidden runtime-COM mechanisms.
6. Exercises missing input, changed-header, unsupported-selected-declaration, and an output path blocked by an existing file; every case requires actionable diagnostics with nonzero results.

On a mismatch, the assertion reports the file, committed and regenerated SHA-256 values, the first differing byte and text line when decodable, both absolute paths, and the exact update command. It never updates committed files. The remedy is to inspect the restored SDK change, run `UpdateWebView2Interop`, review both generated diffs, run ABI/host tests, and commit the SDK pin, specification, source, and manifest together.

The project is an integration test because it crosses the generator, restored NuGet package, official native inputs, filesystem output, manifest contract, and committed production source. It is not a real-WebView2 test and does not belong in any visible, hidden-browser, AOT-execution, or long-running test project.

#### WebView2 SDK and projection update workflow

A WebView2 SDK or projected-surface change is one review unit:

1. Change the centrally pinned `Microsoft.Web.WebView2` version and restore.
2. Update `webview2-interop-spec.json` only when production code needs a different native surface.
3. Run `UpdateWebView2Interop`; never edit either generated file manually.
4. Review the input hashes and selected-surface changes in the manifest before reviewing the generated C# diff.
5. Run the interop-generation integration project and Windows-fast ABI tests.
6. Run the hidden CoreCLR WebView2 project and Native AOT project so declaration correctness is exercised at the real native boundary.
7. Commit the package pin, specification when changed, generated C#, manifest, generator changes, and affected tests together.

Changing output semantics requires incrementing `generatorFormatVersion`. Changing manifest fields or their meaning requires incrementing `schemaVersion`. The tool obtains package identity and version from the restored package metadata and refuses a requested package root whose metadata does not identify `Microsoft.Web.WebView2`. A local NuGet cache modification is visible through the recorded input hashes and cannot silently bless a committed projection.

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

The base shown above is the default application data root. Phase 1 also supports a small deployment-time `nanto.runtime.json` beside the application and a `--data-root` operational override. For Native AOT and self-contained CoreCLR, the application directory contains the executable; for framework-dependent CoreCLR, it is `AppContext.BaseDirectory`, containing the application DLL and runtime configuration files. Resolution never uses the process current directory.

The versioned configuration schema begins with only the storage extension point:

```json
{
  "schemaVersion": 1,
  "dataRoot": "D:\\ApprovedAppData\\MyApp"
}
```

Resolution precedence is:

1. `--data-root <path>` consumed by Nanto's bootstrap argument parser;
2. `dataRoot` in adjacent `nanto.runtime.json`;
3. `%LOCALAPPDATA%\Nanto\applications\<application-key>`.

The selected value replaces the complete application-specific root, so its immediate children are `assets-v1`, `profiles`, and `cache-maintenance.json`; Nanto does not append another application key. Maintenance metadata stores the canonical `ApplicationId` and rejects an existing root belonging to another application.

An absolute configured path is canonically normalized. A relative path resolves against the application directory. Nanto performs no environment-variable, tilde, shell, or command substitution. Empty paths, embedded NULs, malformed JSON, duplicate JSON properties, unsupported schema versions, unknown properties, and configuration files larger than 64 KiB fail before asset extraction or WebView2 creation. The file is UTF-8 JSON and uses a source-generated `System.Text.Json` context.

The command-line override exists for diagnostics, controlled launch scripts, and recovery from a broken adjacent configuration. It follows the same absolute/relative path rules and is removed from arguments exposed to application code. A repeated `--data-root`, missing value, or conflicting spelling is an error. Logging records the selected source and normalized path, never the complete configuration document.

Before publishing assets, the host creates the selected root if permitted, writes and deletes a uniquely named probe file, creates and probes the UDF parent, and reports the exact failing path and Win32/.NET error. It never falls back from a configured root to `%LOCALAPPDATA%`, because doing so would evade deployment policy and split application state. WebView2 still requires a writable UDF when all SPA assets are embedded.

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
--request <absolute-json-path> --report <absolute-json-path> [--data-root <path>]
```

TestProtocol owns the source-generated JSON context and versioned DTOs used by both TestApp and IntegrationTestKit. The JSON request contains scenario, presentation mode (`Hidden` or `Visible`), runtime lane (`Evergreen` only in Phase 1), optional case-sensitive failure-checkpoint name, iteration count, expected runtime-configuration source, UDF, cache, and artifact paths. `--data-root` exercises the production bootstrap override and changes storage location, never test inventory.

The report contains protocol version, scenario, success/failure, host/runtime/architecture information, timestamps and durations, ordered serialized checkpoint names, lifecycle transitions, initial/peak/final ledger snapshots, renderer recovery result, and retained artifact paths. Protocol DTOs expose no internal production enum or exception type.

IntegrationTestKit must:

- create a unique artifact, UDF, and cache directory per process;
- copy the minimal host payload into a unique launch directory for adjacent-configuration scenarios, write `nanto.runtime.json` only there, and never mutate a shared build or publish directory;
- deliberately share only the cache directory in the multi-instance bundle-lease scenario while keeping its UDFs independent;
- provide a separate production multi-instance scenario in which two processes share the same `ApplicationId`, default profile, configured application data root, asset cache, and UDF while using byte-identical WebView2 environment options;
- launch TestApp without a shell and capture stdout/stderr asynchronously;
- assign TestApp and inherited Chromium children to a kill-on-close Job Object;
- use a 45-second default scenario timeout, 15-second renderer recovery timeout, 15-second shutdown timeout, and 10-second browser-exit timeout;
- kill the job on timeout and report it as failure;
- delete successful UDFs only after browser exit, while retaining failure artifacts;
- disable test parallelism in every real-WebView2 project.

Hidden and long-running projects always send `Hidden`; visible tests always send `Visible`. No integration project accepts a profile property, trait filter, or environment variable that changes its test inventory.

## Vertical milestones

1. **Repository and lifecycle foundation**
   - Establish the canonical solution and its safe default/integration configuration matrix.
   - Add production assemblies and fast test projects.
   - Implement state machines, shutdown policy, registry snapshots, cancellation-first shutdown, and cleanup aggregation.
   - Add fake host and dispatcher.

2. **Win32 host**
   - Add a dedicated STA thread, asynchronous dispatcher, message pump, primary `HWND`, and registry.
   - Support activation, focus, resize, close, destroy, and cancellation.
   - Number every native acquisition and test failure cleanup as each resource is introduced.

3. **WebView2 and assets**
   - Add the offline interop generator, reviewed projection specification, committed source/manifest, and byte-for-byte `IntegrationInterop` gate before production host code consumes the projection.
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
   - Execute framework-dependent CoreCLR, self-contained CoreCLR, Native AOT, and interop-generation integration configurations.
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
- Runtime-configuration absence/defaults, absolute and relative `dataRoot`, command-line precedence, malformed/oversized configuration, duplicate arguments, configured-root identity mismatch, and unwritable-root diagnostics.
- Win32/WebView2 ABI declarations and dependency checks preventing UI-framework packages from entering the host.

`Nanto.Testing.Tests` owns:

- `ManualUiDispatcher` explicit draining, nested inline execution, synchronization-context capture/restoration, FIFO order, cancellation, exception propagation, shutdown, and exactly-once invocation.
- `FakeNantoApplicationHost` single-use run, deterministic gates, repeated/concurrent stop, caller-wait cancellation, disposal, failure aggregation, and application event ordering.
- `FakeNantoWindow` recorded mutations, valid transitions, idempotent close, and scripted renderer-failure events.
- `LifecycleRecorder` immutable snapshots, ordering, caller-provided time, and isolation between runs.
- `FailurePlan` ordered matching, deterministic triggering, exhaustion, and diagnostics for unexpected portable operations.
- A dependency test proving the package contains no Windows or test-framework reference and uses no real thread, filesystem, or wall-clock timing.

### WebView2 interop-generation integration

```powershell
dotnet test -c IntegrationInterop
```

- Resolves `WebView2.idl` and `WebView2.h` from the centrally pinned NuGet package without copying them into the repository.
- Regenerates the selected COM projection and manifest twice beneath its ignored `obj/interop-verification/<Configuration>` tree.
- Proves deterministic generation and byte-for-byte equality with committed production files.
- Verifies manifest hashes, selected interface closure, source-generated COM requirements, and fail-closed diagnostics.
- Creates no window, WebView2 environment, browser subprocess, UDF, or persistent artifact output.

`IntegrationInterop` is inexpensive and safe for unattended execution. When change-scoped CI is enabled, it runs for changes to the WebView2 pin, generator, specification, committed generated files, or Windows interop usage.

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
- Starts two complete hosts against the same production UDF/profile, requires both to reach readiness and exchange messages, closes the first, proves the second remains usable, then closes the second and verifies Chromium releases the shared UDF without locked files. Failure requires an explicit single-instance or per-instance-profile policy decision; the test must not silently switch to separate UDFs.

This is the CI-safe real-WebView2 project.

### Native AOT integration

```powershell
dotnet test -c IntegrationAot
```

- Publishes the external hidden test host as `win-x64` Native AOT.
- Runs the critical hidden lifecycle scenarios.
- Fails on AOT/trimming warnings and unexpected dependencies.
- Requires `WebView2LoaderStatic.lib` to resolve the generated loader declarations and verifies that deployment contains and imports no `WebView2Loader.dll`.

### Self-contained CoreCLR integration

```powershell
dotnet test -c IntegrationSelfContained
```

- Publishes the external hidden test host as self-contained `win-x64` CoreCLR.
- Runs the same critical lifecycle, asset, origin, messaging, and teardown scenarios as the framework-dependent hidden lane.
- Verifies the adjacent loader is present and Microsoft-signed, the apphost requires no installed .NET runtime, and publish metadata identifies CoreCLR rather than Native AOT.
- Verifies the deployable directory contains no PDB and the separate symbol artifact exists with its hash recorded.

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
- Requested CoreCLR deployment CI runs `dotnet test -c IntegrationSelfContained`.
- Requested AOT CI runs `dotnet test -c IntegrationAot`.
- Interop-change CI runs `dotnet test -c IntegrationInterop` before compiling the Native AOT host.
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

Acceptance requires the default solution configurations to exclude integration support and integration-test projects and all portable API dependency tests to pass. Nanto must build from a clean clone with no external source repository present.

### Milestone 2 — Win32 host

```powershell
dotnet test -c Integration
```

The hidden project initially covers `UiThreadStarted` through `HwndCreated`, dispatcher work, native close, cancellation, and ledger-zero shutdown. No top-level window may become visible or activated during the run.

### Milestone 3 — WebView2 and assets

```powershell
dotnet test -c IntegrationInterop
dotnet test -c Integration
```

The generated projection and manifest must reproduce byte-for-byte before the host consumes them. The complete 23-checkpoint failure matrix, secure-origin asset fixture, route fallback, navigation, browser-exit synchronization, and renderer-recovery scenarios must pass.

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
dotnet test -c IntegrationSelfContained
dotnet test -c IntegrationAot
```

All normal, injected-failure, close-race, timeout, renderer-failure, and shared-UDF multi-instance reports must end with a zero ledger. Framework-dependent CoreCLR, self-contained CoreCLR, and Native AOT must use isolated build trees and the same production contracts. Native AOT must produce no unexplained trim/AOT warning. Deployable directories contain no PDB; every publish lane retains symbols separately.

### Milestone 6 — Phase 1 gate

```powershell
dotnet build
dotnet test
dotnet test -c IntegrationInterop
dotnet test -c Integration
dotnet test -c IntegrationSelfContained
dotnet test -c IntegrationAot
```

Run `dotnet test -c IntegrationLongRunning` separately only when explicitly approving its machine time. The Phase 1 gate report records the exact commands, SDK/runtime versions, architecture, results, known deferrals, and artifact locations.

## Completion criteria

- Root `dotnet test` in the default configuration never runs integration support code or creates native windows.
- Default `dotnet test` consumes committed interop; `IntegrationInterop` verifies it without modifying source files.
- Hidden integration tests display no windows and can run unattended.
- Visible behavior is isolated in an unmistakably named opt-in project.
- Every acquisition has fault-injection coverage.
- CoreCLR and Native AOT use identical production contracts and ownership paths.
- Framework-dependent CoreCLR, self-contained CoreCLR, and Native AOT use isolated `obj`/`bin` trees and pass their fixed integration configurations.
- Publish directories exclude PDBs while separately retained symbols remain available for diagnostics.
- Default, adjacent-configuration, and command-line data-root resolution fail clearly and never silently fall back.
- Success, failure, close races, and renderer recovery finish with a zero resource ledger.
- Every milestone has a passing commit with its cumulative acceptance commands recorded in the commit or gate evidence.
