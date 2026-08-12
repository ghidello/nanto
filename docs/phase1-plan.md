# Phase 1 — Windows Host and Lifecycle Kernel

## Summary

Phase 1 creates Nanto's production framework foundations as a clean implementation. The feasibility work completed before the rename established that the chosen raw-Win32/WebView2 and Native AOT direction is viable; every requirement adopted from that work is stated explicitly in this document. Nanto does not require another repository, assembly, report, test profile, or experiment to build or to interpret this plan.

Work will use small vertical commits and structurally separated test projects:

- `dotnet test` runs only production fast tests. Windows-fast tests may create bounded hidden raw-Win32 windows on private STA threads, but they must not show or activate a window, start WebView2 or an external process, require desktop interaction, or become long-running.
- All unattended framework-dependent CoreCLR, self-contained CoreCLR, Native AOT, and interop-generation tests run through explicit `*IntegrationTests` projects selected by the repository `TestScope` convention.
- Visible and long-running tests remain explicit projects inside `Nanto.slnx` and run only through direct project commands.
- Repository build configuration assigns the `integration=true` trait once at assembly level to every `*IntegrationTests` project. `TestScope` selects the fast, complete, or integration-only inventory; test profiles and environment variables do not select tests or build modes.

The integration strategy remains hybrid: pure tests for most behavior, hidden production-path WebView2 tests for unattended execution, and a small isolated visible-window project.

## Solution organization

### Root `Nanto.slnx`

This is the single canonical solution and will contain every production, testing, support, and integration project. `Debug` and `Release` retain their ordinary compiler meanings; neither changes project or test inventory. The current fast foundation comprises:

- `Nanto.Core`
- `Nanto.Hosting.Windows`
- `Nanto.Testing` repository test support
- `Nanto.Core.Tests`
- `Nanto.Hosting.Windows.Tests`
- `Nanto.Testing.Tests`

Therefore:

```powershell
dotnet build
dotnet test
```

build production code and run fast tests without creating WebView2 processes, external test processes, or visible or activated windows. The Windows-fast project may create short-lived hidden raw-Win32 windows to exercise the real message and ownership boundary.

The repository-local `tests/Directory.Build.props` assigns an assembly-level `integration=true` trait to every project whose name ends in `IntegrationTests`. Its `TestScope` property has exactly three values:

| `TestScope` | Selected tests |
| --- | --- |
| `Fast` (default) | Runs ordinary fast-test assemblies and intentionally filters integration assemblies to zero tests. |
| `All` | Runs every fast and unattended integration test in the solution. |
| `Integration` | Runs unattended integration assemblies and intentionally filters ordinary fast-test assemblies to zero tests. |

When a scope intentionally filters an assembly to zero tests, its project-specific MTP arguments ignore exit code 8. That exception is applied only to the assemblies excluded by the chosen scope so an accidentally empty participating project still fails. Visible and long-running assemblies additionally receive `manual=true` and remain excluded unless their project is named directly with `TestScope=All` and `RunManualTests=true`; a solution-level manual opt-in fails validation. `TestScope` changes only test selection; each integration project fixes its own runtime and deployment model, and the AOT project explicitly publishes TestApp with Release settings.

Canonical selection therefore remains visible at the command line:

```powershell
dotnet test                              # fast tests only
dotnet test -p:TestScope=All             # fast + every unattended integration project
dotnet test -p:TestScope=Integration     # unattended integration projects only
```

### Explicit Phase 1 integration projects

These projects are members of `Nanto.slnx` and remain visible in the IDE:

- `Nanto.Hosting.Windows.HiddenIntegrationTests`
- `Nanto.Hosting.Windows.CoreClrSelfContainedIntegrationTests`
- `Nanto.Hosting.Windows.AotIntegrationTests`
- `Nanto.Hosting.Windows.InteropGeneration.IntegrationTests`
- `Nanto.Hosting.Windows.VisibleIntegrationTests`
- `Nanto.Hosting.Windows.LongRunningIntegrationTests`

Their support projects—TestProtocol, TestApp, IntegrationTestKit, and the offline WebView2InteropGen tool—are also solution members. All projects remain visible and buildable in ordinary `Debug` and `Release` configurations. The default `Fast` test scope excludes every integration project; `All` runs the complete unattended graph, while `Integration` runs only unattended integration projects. VisibleIntegrationTests and LongRunningIntegrationTests remain manual-only despite their assembly classification and require direct project commands plus `-p:TestScope=All -p:RunManualTests=true`.

## Architecture and contracts

Create:

- `Nanto.Core`: portable application contracts plus the public `Nanto.Hosting` host-authoring surface for lifecycle, validation, identity, asset context, and cleanup.
- `Nanto.Hosting.Windows`: STA host, Win32 window, WebView2, assets, navigation, DPI, logging, and teardown.
- `Nanto.Testing`: non-packable repository test support containing the deterministic fake host/window, manual dispatcher, lifecycle recorder, and portable failure scripting. It is not a Phase 1 product package.

Initial portable API:

- `ApplicationState`: `NotStarted`, `Creating`, `Created`, `Activated`, `Deactivated`, `Closing`, `Closed`, `Failed`.
- `WindowState`: `Created`, `Initializing`, `Running`, `Closing`, `Closed`, `Failed`.
- Immutable GUID-backed `WindowId`.
- DIP-based `WindowBounds`.
- `WindowOptions` for title, initial bounds, visibility, resizability, and route.
- `ShutdownMode`: primary-window, last-surface, or explicit shutdown.
- `INantoWindow`: state, title, bounds, activation, close, and renderer-failure notification.
- `IUiDispatcher`: access check and asynchronous invocation without synchronous UI waits.
- `ColorSchemePreference`: application/profile-wide `System`, `Light`, or `Dark` preference.
- `INantoApplicationHost`: state, dispatcher, primary-window and color-scheme snapshots, lifecycle events, single-use run, appearance mutation, and idempotent stop.
- `IWebAssetProvider`: prepares a fixed validated URL-path inventory with provider-specific content-stability guarantees.

Phase 1 supports one primary window. Multi-window and tray behavior remain later phases.

## Implementation specification

### Project graph and build properties

Use the following exact project layout:

| Project | Target | References and role |
| --- | --- | --- |
| `src/Nanto.Core/Nanto.Core.csproj` | `net10.0` | Portable application and public host-authoring contracts and implementations; references `Microsoft.Extensions.Logging.Abstractions` 10.0.10; sets `IsAotCompatible=true`. |
| `src/Nanto.Hosting.Windows/Nanto.Hosting.Windows.csproj` | `net10.0-windows10.0.19041.0` | References Core, pinned WebView2 SDK assets, CsWin32, and logging abstractions; supports `win-x64`; enables unsafe code, trimming analysis, `DisableRuntimeMarshalling`, and CsWin32 build-task generation. Product policy requires Windows 10 22H2/build 19045 or newer even though the Windows SDK contract version remains 19041. |
| `tests/Nanto.Testing/Nanto.Testing.csproj` | `net10.0` | Non-packable repository support library; references Core only and contains deterministic fakes, recording, and failure scripting without Windows or test-framework types. |
| `tests/Nanto.Core.Tests/Nanto.Core.Tests.csproj` | `net10.0` | References Core and the standard repository test packages. |
| `tests/Nanto.Hosting.Windows.Tests/Nanto.Hosting.Windows.Tests.csproj` | `net10.0-windows10.0.19041.0` | References Core and Windows hosting; may create short-lived hidden raw-Win32 windows on private STA threads, but contains no visible or activated window, WebView2 creation, external-process orchestration, desktop interaction, or long-running scenario. |
| `tests/Nanto.Testing.Tests/Nanto.Testing.Tests.csproj` | `net10.0` | References Core and the repository-local Testing support library. |
| `tests/Nanto.Hosting.Windows.TestProtocol/Nanto.Hosting.Windows.TestProtocol.csproj` | `net10.0` | Integration-only versioned request/report DTOs, scenario names, and serialized checkpoint names; references no production or test-framework assembly and is not a test project. |
| `tests/Nanto.Hosting.Windows.TestApp/Nanto.Hosting.Windows.TestApp.csproj` | `net10.0-windows10.0.19041.0` executable | References Core, Windows hosting, and TestProtocol; external process used by every native integration project; publishable as CoreCLR or Native AOT. |
| `tests/Nanto.Hosting.Windows.IntegrationTestKit/Nanto.Hosting.Windows.IntegrationTestKit.csproj` | `net10.0-windows10.0.19041.0` | References TestProtocol; contains the process runner, artifact handling, job-object containment, and integration assertions; it is not a test project. |
| `eng/Nanto.WebView2InteropGen/Nanto.WebView2InteropGen.csproj` | `net10.0` executable | Offline deterministic generator for the narrow WebView2 COM projection; consumes official inputs from the pinned SDK package and has no production runtime role. |
| `tests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests.csproj` | `net10.0` | References the generator, regenerates into its own `obj` tree, and compares committed outputs byte-for-byte without creating a window or WebView2 process. |
| Each real-WebView2 `*IntegrationTests` project | `net10.0-windows10.0.19041.0` | References IntegrationTestKit and builds or publishes TestApp according to its single fixed execution model. The portable interop-generation project is the deliberate exception. |

Pin `Microsoft.Web.WebView2` at `1.0.4129.50`, `Microsoft.Windows.CsWin32` at `0.3.298`, and `Microsoft.Extensions.Logging.Abstractions` at `10.0.10` in central package management. Upgrade WebView2 and CsWin32 only through an explicit dependency review that includes interop regeneration, ABI verification, AOT/trim diagnostics, loader verification, and real-host integration tests. Do not add `Microsoft.Extensions.Hosting`, a DI container, OpenTelemetry SDK, or a UI framework in Phase 1.

The solution contains the complete project graph. Ordinary solution builds compile that graph under the requested `Debug` or `Release` configuration. Test execution uses `TestScope`; the default fast loop does not execute integration tests, although their projects may still be built as solution members.

The hidden raw-Win32 fast tests are an early Phase 1 placement, not a permanent commitment. After `Nanto.Hosting.Windows.HiddenIntegrationTests` and TestApp exist, measure the default suite and evaluate whether moving those tests to the integration project materially improves execution speed or isolation. Move them only when the measured benefit justifies losing their coverage from ordinary `dotnet test`; do not duplicate the same scenarios indefinitely.

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

Within the complete or integration-only test scope, HiddenIntegrationTests runs framework-dependent CoreCLR TestApp, CoreClrSelfContainedIntegrationTests publishes once to `artifacts/phase1/publish/coreclr-self-contained/win-x64`, and AotIntegrationTests publishes once to `artifacts/phase1/publish/native-aot/win-x64`. Each project fixes `NantoBuildMode` in MSBuild; no trait, filter, environment variable, profile, or test argument changes its build mode.

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

The generator and its integration test are members of `Nanto.slnx`. Ordinary `dotnet build` consumes only committed `WebView2Interop.g.cs`, and default `dotnet test` does not execute regeneration; `dotnet test -p:TestScope=All` includes deterministic regeneration and comparison. A developer may run the interop-generation project directly with `-p:TestScope=All` for a narrow check. The project is safe for unattended CI because it creates no native window, browser process, or desktop interaction.

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
The generator derives the recorded version from the resolved NuGet package root and rejects a root that does not end in a valid package version; the manifest never relies on a separately hard-coded version string.

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

The narrow verification command is:

```powershell
dotnet test tests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests.csproj -p:TestScope=All
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
    Explicit,
}

public readonly record struct WindowId
{
    public Guid Value { get; }

    public WindowId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
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

`WindowBounds` uses DIPs. Construction and mutation reject non-finite values and non-positive width or height with `ArgumentOutOfRangeException`; negative X/Y values are valid for monitors left of or above the primary monitor. The portable type imposes no arbitrary maximum on finite coordinates or dimensions. At each platform boundary, Nanto performs checked DIP-to-native conversion using the applicable DPI and rejects any rounded value that cannot be represented by the native coordinate type before calling the platform API. `WindowId.Create` must never return `Guid.Empty`. The CLR can still produce default values for both structs; `default(WindowId)` and `default(WindowBounds)` are invalid sentinels and every public boundary must reject them.

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
    public ColorSchemePreference PreferredColorScheme { get; init; } = ColorSchemePreference.System;
    public ShutdownMode ShutdownMode { get; init; } = ShutdownMode.OnPrimaryWindowClosed;
    public ILoggerFactory? LoggerFactory { get; init; }
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
```

Option initializers remain data-only. `RunAsync` validates the complete immutable option graph before acquiring native resources: `Title` must contain a non-whitespace character; `InitialBounds` must not be the invalid default value; `InitialRoute` must be root-relative and reject absolute, scheme-relative, backslash-containing, dot-segment, or encoded-traversal paths; `ApplicationId`, `PrimaryWindow`, and `Assets` are required; `PreferredColorScheme` and `ShutdownMode` must be defined enum values; and `ShutdownTimeout` must be positive and no greater than five minutes. A null `LoggerFactory` becomes `NullLoggerFactory.Instance` internally.

`ApplicationId` is trimmed, canonicalized to lowercase invariant, and must contain at least two dot-separated ASCII segments. Each segment is 1–63 characters, the complete identifier is at most 253 characters, and segments contain only letters, digits, and interior hyphens. It is a persistent storage and security boundary, not a display label. Nanto derives `<application-key>` from the final readable segment plus the first 128 bits of SHA-256 over the canonical UTF-8 identifier, stores the complete canonical identity in cache metadata, and refuses a metadata mismatch. Renaming an executable, assembly, or product display name does not change this identity; changing `ApplicationId` intentionally starts with a new cache and WebView2 profile.

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
    public required ApplicationState OldState { get; init; }
    public required ApplicationState NewState { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public Exception? Failure { get; init; }
}

public sealed class WindowStateChangedEventArgs : EventArgs
{
    public required WindowState OldState { get; init; }
    public required WindowState NewState { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public Exception? Failure { get; init; }
}

public sealed class RendererFailedEventArgs : EventArgs
{
    public required RendererFailureKind Kind { get; init; }
    public required string Description { get; init; }
    public required bool WillAttemptRecovery { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

public sealed class NantoHostException : Exception
{
    public required NantoFailureStage Stage { get; init; }
    public required string Operation { get; init; }
    public int? NativeErrorCode { get; init; }
    public IReadOnlyList<Exception> CleanupExceptions { get; init; } = [];

    public NantoHostException(string message, Exception primaryFailure)
        : base(message, primaryFailure)
    {
    }
}
```

All timestamps are UTC `DateTimeOffset` values. Portable lifecycle implementations receive a `TimeProvider` through an internal constructor seam; production uses `TimeProvider.System`, while `Nanto.Testing` supplies deterministic time. Required init accessors validate non-empty descriptions and operation names and UTC-compatible timestamps. `NantoHostException` validates its message and non-null primary failure in its constructor and defensively copies any cleanup-exception collection in its init accessor before exposing it.

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
    INantoWindow? PrimaryWindow { get; }
    ColorSchemePreference PreferredColorScheme { get; }
    event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;
    Task RunAsync(NantoApplicationOptions options, CancellationToken cancellationToken = default);
    ValueTask SetPreferredColorSchemeAsync(ColorSchemePreference preferredColorScheme, CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public interface IWebAssetProvider
{
    ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default);
}

public sealed class WebAssetPreparationContext
{
    public string ApplicationId { get; }
    public string ApplicationStorageKey { get; }

    internal WebAssetPreparationContext(string applicationId, string applicationStorageKey)
    {
        ApplicationId = applicationId;
        ApplicationStorageKey = applicationStorageKey;
    }
}

public interface IWebAssetLease : IDisposable
{
    string RootDirectory { get; }
    string Version { get; }
    IReadOnlySet<string> AssetPaths { get; }
}
```

The `Nanto.Hosting` namespace in `Nanto.Core` is the supported, platform-free surface for implementing a host. It exposes `ApplicationIdentity`, `ValidatedApplicationOptions`, `ApplicationLifecycle`, `WindowLifecycle`, and `AsyncCleanupRegistry`. Application code continues to use the contracts in `Nanto`; the host-authoring types are public so first-party and future external platform hosts can share the canonical lifecycle, validation, identity, and cleanup machinery without friend access or duplicated rules.

`ApplicationIdentity` and `ValidatedApplicationOptions` have no public constructors. Hosts call `ApplicationIdentity.Parse` or, normally, `ValidatedApplicationOptions.Create`; validated options then create the corresponding `WebAssetPreparationContext`. This prevents a host or provider from pairing an application ID with an inconsistent storage key. Provider implementations can read the context's public properties but cannot construct it or independently derive a different key. A directory provider may ignore the storage key, while the versioned provider uses it beneath the platform cache root. Route and timestamp validation helpers remain internal implementation details.

State-change event arguments contain old state, new state, timestamp, and an optional failure object. Events are raised synchronously on the UI thread after the state field changes. Event handlers are diagnostic notifications: an exception from one handler is logged and does not prevent later handlers or teardown.

`RendererFailedEventArgs` contains a portable `RendererFailureKind`, a diagnostic description, whether recovery will be attempted, and the occurrence timestamp. It exposes no WebView2 enum or COM value.

`WindowsApplicationHost` is the public sealed implementation of `INantoApplicationHost` and has a public parameterless constructor. Construction stores no native resources and starts no thread; the dedicated UI thread and dispatcher are created by the first and only `RunAsync` call. Before that point, reading `Dispatcher` throws `InvalidOperationException`. After shutdown it returns the same dispatcher instance in its disposed state. `PrimaryWindow` is null before creation and after teardown and otherwise returns the current window reference through a safely published snapshot.

`INantoApplicationHost.State`, `PrimaryWindow`, `PreferredColorScheme`, and every `INantoWindow` property are safe to read from any thread. Implementations publish immutable snapshots with explicit memory visibility; callers never dispatch merely to inspect state. Mutating methods remain asynchronous and marshal to the owning dispatcher internally. Lifecycle and renderer events are raised synchronously on the UI thread as specified above.

`PreferredColorScheme` is application/profile-scoped because WebView2 applies the corresponding preference to every WebView that shares the profile. `System` follows the operating-system preference; `Light` and `Dark` override it. The application owns persistence and supplies its saved preference on the next run. `SetPreferredColorSchemeAsync` is valid after application creation, updates the snapshot only after the platform accepts the change, treats the existing value as a no-op, throws `InvalidOperationException` before creation, and throws `ObjectDisposedException` during or after closure. Web content observes the effective result through standard CSS `prefers-color-scheme` and `matchMedia`; Nanto does not add a theme-specific frontend protocol.

The built-in asset providers use these construction entry points:

- `DirectoryWebAssetProvider(string rootDirectory)` captures an absolute directory path and validates its lease contents during `PrepareAsync`.
- `VersionedWebAssetProvider.FromAssembly<TMarker>(string manifestResourceName)` uses `typeof(TMarker).Assembly` as an explicit AOT-safe resource owner and loads the named manifest resource without assembly scanning. The returned provider opens only the exact manifest and resource names declared by that manifest.

Neither provider acquires a lease or mutates the filesystem in its constructor or factory. `PrepareAsync` owns validation and acquisition, and the returned lease owns any filesystem handle it creates. Every lease fixes its validated URL-path inventory. A `DirectoryWebAssetProvider` lease does not copy, hash, watch, or make the underlying file bytes immutable; callers must not add, remove, or rename files while it is active. Strict byte immutability belongs to the versioned provider.

### Portable testing toolkit

`Nanto.Testing` is a non-packable, repository-local, test-framework-neutral support assembly. It references `Nanto.Core` only and contains no xUnit, assertion-library, Windows, filesystem, real-thread, or wall-clock dependency. It provides:

- `ManualUiDispatcher`, which queues work until `RunNextAsync` or `DrainAsync` is called and exposes `WaitForPendingWorkAsync` for deterministic coordination without polling; while draining it temporarily installs its own synchronization context, callbacks observe dispatcher access, nested calls execute inline, posted continuations return to its queue, and the caller's prior context is restored afterward.
- `FakeNantoApplicationHost`, which uses the production portable lifecycle state machine and exposes deterministic gates for creation, activation, failure, stop, and close.
- `FakeNantoWindow`, which uses the production window state machine, records title/bounds/activation calls, and can raise a scripted renderer failure.
- `LifecycleRecorder`, which records immutable ordered application, window, and renderer events with timestamps supplied by a caller-provided `TimeProvider`.
- `FailurePlan`, which scripts failures at named portable operations such as application creation, window initialization, activation, and close. Windows acquisition checkpoints do not enter this package.

These utilities expose recorded calls and state; they do not provide assertion methods or throw test-framework-specific exceptions. `Nanto.Testing.Tests` tests the toolkit itself rather than duplicating Core or Windows-host tests.

The fake host's creation, activation, failure, stop, and close gates start open. A test closes a gate before the relevant operation, awaits its reached signal to observe the stable checkpoint, and opens it to continue; cancellation races creation and activation gates so shutdown never depends on a test releasing them. Portable failure-plan operation names are `application.create`, `window.initialize`, `application.activate`, `window.set-title`, `window.set-bounds`, `window.activate`, and `window.close`. A plan records every observed operation, including permissive plans and mismatches, and strict plans require exact ordinal order.

`Nanto.Hosting.Windows` and `Nanto.Testing` consume the public `Nanto.Hosting` surface and receive no friend access to Core. `Nanto.Core.Tests` should verify behavior through public contracts by default, but Core may grant that test project friend access when direct coverage of internal edge cases is useful and avoids awkward production API exposure. Such access is test-only and must not be used by production or repository testing-support assemblies. Tests receive no friend access to the Windows host beyond `Nanto.Hosting.Windows.Tests` and the Phase 1 TestApp seams described below; portable Core remains free of Windows types.

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

- `RunAsync` is single-use and normally returns only after `Closed`. A second invocation throws `InvalidOperationException`.
- `RunAsync` after `DisposeAsync` throws `ObjectDisposedException`. An already-canceled run token still starts the UI lifecycle, transitions from `Creating` to `Closing`, completes deterministic cleanup, and reaches `Closed` without creating a window or WebView.
- Cancellation of the `RunAsync` token requests orderly shutdown; it does not abandon cleanup. `RunAsync` completes normally if shutdown succeeds.
- `StopAsync` before `RunAsync` is a completed no-op. Once `RunAsync` has atomically claimed the host, every `StopAsync` call requests application shutdown exactly once. Its cancellation token cancels only that caller's wait, including when the token was already canceled; the shutdown request is still recorded, teardown continues, and completion remains observable through `RunAsync`. `StopAsync` after `Closed` completes immediately.
- `INantoWindow.CloseAsync` independently requests that window's close exactly once. Its cancellation token likewise cancels only the caller's wait, including when already canceled. A primary-window close and an application stop may race safely and converge on the same idempotent window and application teardown paths.
- `DisposeAsync` before `RunAsync` marks the host disposed without starting a UI thread or raising lifecycle events; `State` remains `NotStarted`. During execution it requests stop and waits without caller cancellation for the same completion as `RunAsync`. After closure and on repeated or concurrent calls it observes that same completion immediately.
- The validated `ShutdownTimeout` is one deadline beginning with the first shutdown request and spanning application-lifetime cancellation callbacks, any still-pending native startup callback, the transition to `Closing`, native and managed cleanup, the transition to `Closed`, dispatcher/UI-thread termination, and final host-lease release. Stop signaling and the deadline begin before cancellation callbacks are invoked. A callback failure is preserved in the host failure result without preventing shutdown progress; a callback that does not return remains subject to the same deadline. If that deadline expires, `RunAsync`, `StopAsync`, and `DisposeAsync` observe a teardown `NantoHostException` promptly; the host retains ownership and continues observing cleanup in the background, logs any later teardown failure, and still attempts to reach `Closed` and release every resource. A pending WebView2 callback remains rooted and keeps its STA alive after public timeout until native completion permits safe rollback. The external integration scenario timeout remains the final process-containment backstop for code that cannot be interrupted cooperatively.
- Startup/runtime/teardown failure is represented by `NantoHostException`, retaining the first failure and any later distinct cleanup failures. Except for the configured shutdown-timeout case above, the host attempts to reach `Closed` before `RunAsync` throws.
- With `OnPrimaryWindowClosed`, closing the primary window requests application shutdown. With `Explicit`, the application message loop remains active with no primary window until `StopAsync` or cancellation.
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

Publication writes only beneath a unique `staging` child, rejects reparse points in every existing path component, flushes and closes all content and metadata, writes `complete` last, and atomically renames the staging directory to `<bundle-sha256>`. A concurrent winner is reusable only after full validation. Validation requires the expected application identity and bundle hash, the exact declared file set with no additional content files, matching lengths and hashes, a matching completion marker, regular non-reparse-point files, and paths that remain beneath the bundle root. An identity mismatch fails without modification; other incomplete or corrupt destinations follow the locked quarantine-and-reconstruct policy below.

The versioned provider uses this application-scoped schema:

```text
%LOCALAPPDATA%\Nanto\applications\<application-key>\
├── application.json
├── assets-v1\
│   ├── <bundle-sha256>\
│   │   ├── content\...
│   │   ├── manifest.json
│   │   ├── complete
│   │   └── lease.lock
│   ├── staging\
│   └── quarantine\
└── profiles\default\webview2-udf\
```

Phase 1 always uses the application-specific root shown above. It has no adjacent `nanto.runtime.json`, `--data-root` argument, environment-variable override, or public storage-location option. Before publishing assets, the host creates the application root if permitted, writes and deletes a uniquely named probe file, creates and probes the UDF parent, and reports the exact failing path and Win32/.NET error. WebView2 requires a writable UDF even when all SPA assets are embedded.

`application.json` is atomically created with a versioned schema and the complete canonical `ApplicationId`. An existing identity mismatch fails startup and is never quarantined, overwritten, or treated as cache corruption. This protects against an implementation error or hash collision crossing application storage boundaries.

`<bundle-sha256>` covers the normalized manifest and every declared asset, not the application release version. Bundle content is immutable after atomic publication. Releases with identical frontend content reuse the same bundle, while different releases may keep different bundles concurrently. The stable UDF is application/profile-scoped rather than release-scoped so browser storage survives an upgrade.

Every prepared versioned lease holds a read handle to `lease.lock` with sharing that permits other readers but denies deletion. Multiple processes using the same bundle therefore coexist; the bundle remains protected until the last process releases its handle. The Windows window owns the lease and disposes it only after removing WebView mappings and closing the controller. Windows releases the handle after abnormal process termination.

A per-application cross-process maintenance lock serializes lease acquisition, destination validation, quarantine, and publication. A valid complete destination is reused. An incomplete or corrupt destination whose metadata belongs to the current application is atomically renamed to a unique child of `assets-v1\quarantine` before a new staging publication begins; it is never edited or deleted in place. If quarantine or reconstruction fails, startup reports the exact path and error. Identity mismatch always fails without modifying the destination.

Phase 1 does not automatically remove old valid bundles, abandoned staging directories, or quarantined bundles. Automatic retention, age-based cleanup, configurable data roots, and enterprise storage policy are deferred and recorded in the architecture roadmap. The immutable bundle layout and leases keep future cleanup possible without changing the asset-provider contract.

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
    └── reverse-order AsyncCleanupRegistry
```

`WindowsApplicationHost` owns the UI thread, COM initialization, window class, shared environment, registry, and application cleanup. `WindowsWindow` owns its `HWND`, asset lease, controller, WebView, subscriptions, and window cancellation. Borrowers never release native resources.

`WindowRegistry` mutates only on the UI thread and publishes immutable array snapshots through `Volatile.Write`; readers never observe an in-progress mutation. Phase 1 rejects creation of a second window with `NotSupportedException`.

Every native/COM operation is wrapped at a narrow boundary that includes operation name and HRESULT or Win32 error in `NantoHostException`. Do not catch and ignore broad exceptions during teardown.

The production presenter calls `ShowWindow`, activation, and foreground APIs only when `WindowOptions.StartVisible` is true. Hidden TestApp scenarios set `StartVisible=false` and use that same presenter, ordinary `WS_OVERLAPPEDWINDOW`, normal WebView2 controller, bounds, navigation, and teardown path. There is no test-only presenter or `InternalsVisibleTo` presentation seam.

### Renderer recovery

- Subscribe to `ProcessFailed` before initial navigation.
- For a renderer-process exit, raise `RendererFailed` and attempt exactly one `Reload` for that occurrence.
- Recovery succeeds only when navigation and the internal readiness signal complete within 15 seconds.
- If reload fails, times out, or immediately produces another renderer failure, transition the window to `Failed` and enter normal closing.
- Browser-process exit is not treated as renderer recovery; it fails the current environment/window, tears down fully, and reports the failure. Automatic whole-host recreation remains outside Phase 1 production behavior.

### Logging and resource ledger

Use source-generated `LoggerMessage` methods with stable numeric event IDs grouped by application lifecycle, window lifecycle, dispatcher, WebView, assets, and teardown. Log symbolic operation names, states, window IDs, HRESULTs, and elapsed durations. Never log web-message bodies, command payloads, cookies, local-storage values, or file contents.

The internal debug ledger tracks application hosts, UI threads, windows, native handles, COM objects, subscriptions, dispatcher items, asset leases, virtual-host mappings, and browser processes. Each lease receives a stable process-local ID. Acquisition and release are paired in the owning component. Counts remain available without retaining an unbounded history; TestApp explicitly enables the diagnostic ownership trace and serializes named acquisitions and releases in order alongside the initial, peak, and final snapshots. A successful or expected-failure scenario requires every owned count except the process's baseline OS handle count to return to zero. The external oracle verifies the documented dependency order within and across scopes: subscriptions and mapping precede controller/COM release; the controller precedes its parent `HWND`; window class follows the window; the application asset lease precedes the shared environment; and the UI thread stops last. Dispatcher items remain independently scoped queue operations and are instead required to be paired and zero at completion.

### Failure-injection checkpoints

Define `Phase1AcquisitionCheckpoint` and the failure-injector interface as internal members of `Nanto.Hosting.Windows`. The production default injector never fails. Grant friend access to Windows-fast tests for their bounded raw-Win32 failure cases and to TestApp so it can install an injector and map TestProtocol's serialized checkpoint name to the internal enum; IntegrationTestKit never references the production assembly. Unknown or incorrectly cased checkpoint names are rejected before the host starts. Tests inject immediately after the named acquisition succeeds.

Every implementation change that introduces an owned acquisition adds a named checkpoint in creation order and its corresponding failure-path assertion in the same commit. The expected areas include UI-thread/COM setup, dispatcher and Win32 registration, `HWND`, asset lease, WebView2 environment/controller/control, settings, subscriptions and filters, virtual-host mapping, and initial navigation. The definitive enum names and count evolve with the implementation rather than being frozen before the ownership graph exists.

The failure and cancellation matrices enumerate the implemented checkpoints, run one external process per applicable checkpoint, prevent every later acquisition, and assert that all earlier acquisitions are released in documented dependency order. Failure scenarios throw from the injector; cancellation scenarios only cancel the real run token, and production must observe that cancellation after the checkpoint before performing another acquisition. A checkpoint without an executable scenario fails coverage; a checkpoint that genuinely does not apply to a scenario is reported as not reached rather than silently passing.

### Integration harness protocol

TestApp accepts only:

```text
--request <absolute-json-path> --report <absolute-json-path>
```

TestProtocol owns the source-generated JSON context and versioned DTOs used by both TestApp and IntegrationTestKit. The JSON request contains scenario, presentation mode (`Hidden` or `Visible`), canonical test `ApplicationId`, optional case-sensitive failure-checkpoint name, iteration count, and artifact directory. TestApp derives the production cache and UDF paths from the fixed default application root; the request cannot override them. TestApp always uses the installed Evergreen WebView2 Runtime in Phase 1, so the protocol has no runtime-lane field until multiple lanes exist.

The report contains protocol version, scenario, success/failure, host/runtime/architecture information, timestamps and durations, ordered serialized checkpoint names, lifecycle transitions, initial/peak/final ledger snapshots, the ordered resource-ownership trace, renderer recovery result, retained artifact paths, and a structured observed failure with every cleanup failure. Protocol DTOs expose no internal production enum or exception type.

IntegrationTestKit must:

- create a unique artifact directory and valid test `ApplicationId` per isolated scenario, then calculate the expected production application root without overriding it;
- copy the minimal host payload into a unique launch directory for deployment scenarios and never mutate a shared build or publish directory;
- provide a production multi-instance scenario in which two processes share the same `ApplicationId`, default application root, asset cache, profile, and UDF while using byte-identical WebView2 environment options;
- launch TestApp without a shell and capture stdout/stderr asynchronously;
- assign each isolated TestApp and its inherited Chromium children to a kill-on-close Job Object; the multi-instance scenario assigns both participants to one parent-owned job so one participant exiting cannot terminate browser processes still shared by the survivor;
- use a 45-second default scenario timeout, 15-second renderer recovery timeout, 15-second shutdown timeout, and 10-second browser-exit timeout;
- kill the job on timeout and report it as failure;
- delete the successful scenario's complete test application root only after browser exit and asset-lease release, while retaining failure roots and artifacts;
- disable test parallelism in every real-WebView2 project.

Hidden and long-running projects always send `Hidden`; visible tests always send `Visible`. Each integration project has a fixed test inventory and build mode. Traits are descriptive, and a runner filter may narrow a developer's diagnostic execution, but neither changes project setup or host behavior. No profile property or environment variable selects tests.

## Vertical milestones

1. **Repository and lifecycle foundation**
   - Establish the canonical solution and its safe repository-level test-scope convention.
   - Add the two production assemblies, repository-local Testing support, and fast test projects.
   - Implement state machines, primary-window snapshots, cancellation-first shutdown, and cleanup aggregation.
   - Add fake host and dispatcher.

2. **Win32 host**
   - Add a dedicated STA thread, asynchronous dispatcher, message pump, primary `HWND`, and registry.
   - Support activation, focus, resize, close, destroy, and cancellation.
   - Number every native acquisition and test failure cleanup as each resource is introduced.

3. **WebView2 interop and minimal host**
   - Add the offline interop generator, reviewed projection specification, committed source/manifest, and byte-for-byte interop-generation gate before production host code consumes the projection.
   - Add application identity metadata, the fixed application root and WebView2 UDF, the directory asset provider, shared environment, and per-window controller ownership.
   - Navigate a minimal directory fixture through the secure virtual HTTPS origin with `DenyCors` and exact-origin validation.
   - Apply the application/profile-wide color scheme before navigation and support live changes through standard `prefers-color-scheme` propagation.
   - Use source-generated COM with runtime marshalling disabled.
   - Keep asynchronous environment/controller callbacks rooted through native completion. Cancellation marks the managed operation but does not abandon the native callback; when completion arrives, a returned COM pointer is released on the STA thread and the waiter settles as canceled without resuming initialization.
   - Teardown by dependency: remove message and navigation subscriptions, clear the mapping, close the controller, release profile/WebView/controller, destroy the `HWND`, unregister its class, dispose the directory lease, release the shared environment, and finally stop the UI thread.

4. **Assets and navigation**
   - Add the versioned extracted asset provider.
   - Add manifest validation, content-addressed publication, shared leases, and quarantine/reconstruction beneath the already established application root.
   - Add manifest-aware routing, SPA fallback, and navigation normalization.

5. **DPI, recovery, and diagnostics**
   - Convert DIPs only at the Windows boundary.
   - Handle resize, DPI changes, minimum sizes, work areas, activation, focus, and negative monitor coordinates.
   - Keep cached DPI UI-thread-owned.
   - Raise a portable renderer-failure event, attempt one reload, and close normally if recovery fails.
   - Synchronize the Win32 title bar and non-client frame with explicit color preferences and operating-system changes while using `System`.
   - Complete subscription removal, controller closure, COM release, browser-exit synchronization, `HWND` destruction, and dispatcher shutdown.
   - Add structured logging and a development resource ledger without logging frontend payloads.

6. **Deployment modes and Phase 1 gate**
   - Add self-contained CoreCLR and Native AOT publish/smoke projects while keeping the exhaustive behavioral matrix in framework-dependent CoreCLR.
   - Execute the complete unattended test scope, which covers framework-dependent CoreCLR, self-contained CoreCLR, Native AOT, and interop generation.
   - Confirm no UI-framework dependencies or unexplained trimming/AOT warnings.
   - Update README, `AGENTS.md`, and testing documentation with project-level commands.

## Test projects

The canonical unattended integration command is `dotnet test -p:TestScope=All`; it runs every fast test and every unattended integration project. Use `dotnet test -p:TestScope=Integration` when diagnosing only the integration inventory. The direct project commands below are narrow developer checks and do not replace the complete phase gate.

### Default fast tests

`dotnet test` runs the three fast projects with non-overlapping ownership.

`Nanto.Core.Tests` owns:

- `WindowId`, `WindowBounds`, options, route, and default-value validation.
- Public host-authoring boundary, canonical application/window transition matrices, invalid transitions, shutdown modes, single-use run, stop/close/disposal races, pre-canceled caller waits that still request shutdown, cancellation during initialization, event ordering, timestamping, and failure aggregation.
- Cleanup ordering, idempotence, continued cleanup after errors, and aggregation.
- Application-identity trimming/lowercasing, segment and total-length boundaries, invalid-character rejection, and storage-key stability.
- Portable manifest-path normalization and the complete navigation golden-decision table.
- Dependency checks preventing platform types from entering portable APIs.

`Nanto.Hosting.Windows.Tests` owns:

- Production `WindowsApplicationHost` single-use startup, primary-window publication, shutdown modes, caller-wait cancellation, lifecycle ordering, acquisition-failure rollback, and ledger-zero teardown.
- Windows dispatcher affinity, synchronization-context capture, FIFO ordering, cancellation, exception propagation, and rejection during shutdown.
- Short-lived hidden raw-Win32 class and window creation, production `WindowsWindow` lifecycle and snapshot behavior, non-activation, native close delivery, caller-wait cancellation, ownership ordering, idempotent destruction, and acquisition-failure rollback on private STA threads.
- Immutable registry snapshots and second-window rejection.
- DIP conversion at common and fractional DPIs, window-message decoding, and monitor/work-area calculations.
- Embedded-manifest parsing, resource validation, bundle hashing, materialization, exact-file validation, traversal/reparse-point rejection, and concurrent publication.
- Cache publication, application identity, shared leases, corrupt-bundle quarantine/reconstruction, quarantine failure, and identity-mismatch refusal.
- Default `%LOCALAPPDATA%` application-root creation, write probes, path-length boundaries, and unwritable-root diagnostics.
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
dotnet test tests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests.csproj -p:TestScope=All
```

- Resolves `WebView2.idl` and `WebView2.h` from the centrally pinned NuGet package without copying them into the repository.
- Regenerates the selected COM projection and manifest twice beneath its ignored `obj/interop-verification/<Configuration>` tree.
- Proves deterministic generation and byte-for-byte equality with committed production files.
- Verifies manifest hashes, selected interface closure, source-generated COM requirements, and fail-closed diagnostics.
- Creates no window, WebView2 environment, browser subprocess, UDF, or persistent artifact output.

This project is inexpensive and safe for unattended execution. Developers may run it directly while changing the WebView2 pin, generator, specification, committed generated files, or Windows interop usage; the complete unattended gate includes it automatically.

### Hidden WebView2 integration

```powershell
dotnet test tests/Nanto.Hosting.Windows.HiddenIntegrationTests/Nanto.Hosting.Windows.HiddenIntegrationTests.csproj -p:TestScope=All
```

- Creates the normal production parent `HWND`.
- Never calls `ShowWindow` or activates the window.
- Uses the normal non-composition WebView2 controller.
- Runs scenarios in isolated child processes.
- Tests navigation, assets, routing, renderer recovery, close races, failure injection, browser exit, and zero-resource teardown.
- In Milestone 3, specifically proves the exact secure origin, navigation-gated startup, internal readiness diagnostics, initial Dark and Light `matchMedia` observations, live profile switching, System acceptance without OS mutation, every startup acquisition failure/cancellation checkpoint, dependency-ordered cleanup, process exit, unlocked storage, and a zero final ledger.
- Starts two complete hosts against the same production application root, directory assets, UDF, and profile; requires both to reach readiness and exchange messages; closes the first and proves the second remains usable; then closes the second and verifies Chromium releases the application root for deletion without locked files. Versioned bundle lease-lock and deletion-denial coverage belongs to Milestone 4. Failure requires an explicit single-instance or per-instance-profile policy decision; the test must not silently switch to separate roots or UDFs.

This is the CI-safe real-WebView2 project.

### Native AOT integration

```powershell
dotnet test tests/Nanto.Hosting.Windows.AotIntegrationTests/Nanto.Hosting.Windows.AotIntegrationTests.csproj -p:TestScope=All
```

- Publishes the external hidden test host as `win-x64` Native AOT.
- Runs the critical hidden lifecycle scenarios.
- Fails on AOT/trimming warnings and unexpected dependencies.
- Requires `WebView2LoaderStatic.lib` to resolve the generated loader declarations and verifies that deployment contains and imports no `WebView2Loader.dll`.

### Self-contained CoreCLR integration

```powershell
dotnet test tests/Nanto.Hosting.Windows.CoreClrSelfContainedIntegrationTests/Nanto.Hosting.Windows.CoreClrSelfContainedIntegrationTests.csproj -p:TestScope=All
```

- Publishes the external hidden test host as self-contained `win-x64` CoreCLR.
- Runs a critical lifecycle, asset, origin, messaging, and teardown subset drawn from the exhaustive framework-dependent hidden lane.
- Verifies the adjacent loader is present and Microsoft-signed, the apphost requires no installed .NET runtime, and publish metadata identifies CoreCLR rather than Native AOT.
- Verifies the deployable directory contains no PDB and the separate symbol artifact exists with its hash recorded.

HiddenIntegrationTests owns the exhaustive behavioral and failure matrix. The self-contained CoreCLR and Native AOT projects intentionally run only the critical startup, secure navigation, messaging, shutdown, loader, symbol, and zero-ledger smoke scenarios needed to prove that their deployment modes preserve the same production contracts.

### Visible desktop integration

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests/Nanto.Hosting.Windows.VisibleIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

This project intentionally shows windows and covers only:

- Actual foreground activation and focus.
- Visible resizing and DWM behavior.
- Real cross-monitor `WM_DPICHANGED`.
- Initial monitor placement.
- Native input and screenshots.

It is excluded from every unattended test scope and ordinary CI. It runs only when someone names the project directly with `RunManualTests=true` on an isolated VM/session and intentionally accepts desktop interaction.

### Long-running integration tests

```powershell
dotnet test tests/Nanto.Hosting.Windows.LongRunningIntegrationTests/Nanto.Hosting.Windows.LongRunningIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

- Hidden windows only.
- Fixed documented soak counts.
- Repeated process isolation, lifecycle, and renderer recovery.
- Manual-only.

## CI and documentation policy

- Ordinary CI runs root `dotnet build` and `dotnet test` using the default `Fast` test scope.
- Requested unattended integration CI runs `dotnet test -p:TestScope=All`, covering interop generation and all three host deployment modes.
- VisibleIntegrationTests and LongRunningIntegrationTests never run automatically.
- Project boundaries define runtime, deployment, visibility, and duration. Repository-generated assembly traits and centrally supplied MTP filters implement the canonical scopes; individual tests do not select their own inventory.
- Test profiles and environment variables do not select tests or host build modes. Environment variables remain acceptable only for genuinely machine-specific inputs that are explicit in the relevant project contract.
- Automatic GitHub Actions triggers remain disabled until a separate cost-policy decision enables them.

Hidden integration remains desktop-hosted even though no window is shown: it must run under an ordinary logged-on Windows user session capable of creating Chromium renderer/GPU processes. Do not run it as a Windows service or in a restrictive process sandbox.

## Milestone acceptance commands

Each milestone is committed only after its listed commands pass. These commands are cumulative.

### Milestone 1 — repository and lifecycle foundation

```powershell
dotnet build
dotnet test
```

Acceptance requires the default test scope to execute only the fast inventory and all portable API dependency tests to pass. Nanto must build from a clean clone with no external source repository present.

### Milestone 2 — Win32 host

```powershell
dotnet test -p:TestScope=All
```

The hidden project initially covers every implemented acquisition through `HWND` creation, dispatcher work, native close, cancellation, and ledger-zero shutdown. No top-level window may become visible or activated during the run.

### Milestone 3 — WebView2 interop and minimal host

```powershell
dotnet test tests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests/Nanto.Hosting.Windows.InteropGeneration.IntegrationTests.csproj -p:TestScope=All
dotnet test -p:TestScope=All
```

The generated projection and manifest reproduce byte-for-byte before the host consumes them. The generator proves base-interface and same-interface vtable-prefix closure, IDL/header agreement, deterministic bytes, manifest hashes, and fail-closed behavior for missing inputs, ABI disagreement, unsupported selections, and blocked output. A hidden production presenter creates the environment, controller, WebView, profile, secure settings, subscriptions, and `DenyCors` mapping; navigates the minimal directory fixture through `https://app.nanto.invalid`; enters `Running` only after successful navigation; observes Dark/Light and live switching through `matchMedia`; accepts System without changing OS settings; cancels cleanly after every acquisition without an injected exception; returns every implemented resource to zero in dependency order; and proves that two hosts can share the production UDF while the survivor remains usable after the first exits. Successful isolated roots are deleted only after process-tree exit, so deletion is also the storage-unlock assertion.

### Milestone 4 — assets and navigation

```powershell
dotnet test -p:TestScope=All
```

Manifest validation, content hashing, atomic publication, concurrent reuse, shared leases, corrupt-bundle quarantine/reconstruction, identity-mismatch refusal, secure-origin asset loading, route fallback, and navigation normalization must pass.

### Milestone 5 — DPI, recovery, and diagnostics

```powershell
dotnet test -p:TestScope=All
```

All synthetic DPI/message tests and the complete implemented failure-checkpoint, close-race, timeout, renderer-recovery, browser-exit, and shared-UDF multi-instance scenarios must pass with a zero final ledger. The visible integration project is then run once on an isolated multi-monitor session and its environment/topology is recorded with the artifacts:

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests/Nanto.Hosting.Windows.VisibleIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

### Milestone 6 — deployment modes and Phase 1 gate

```powershell
dotnet build
dotnet test
dotnet test -p:TestScope=All
```

Framework-dependent CoreCLR runs the exhaustive behavioral suite. Self-contained CoreCLR and Native AOT pass their critical deployment smoke, use isolated build trees and the same production contracts, and validate their loader and symbol policies. Native AOT produces no unexplained trim/AOT warning, deployable directories contain no PDB, and each publish lane retains symbols separately.

Run `dotnet test tests/Nanto.Hosting.Windows.LongRunningIntegrationTests/Nanto.Hosting.Windows.LongRunningIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true` separately only when explicitly approving its machine time. The Phase 1 gate report records the exact commands, SDK/runtime versions, architecture, results, known deferrals, and artifact locations.

## Completion criteria

- Root `dotnet test` in the default scope never executes integration tests, creates WebView2 or external test processes, or shows or activates a window; bounded fast tests may create hidden raw-Win32 windows on private STA threads.
- Default `dotnet test` consumes committed interop; the complete unattended test scope verifies it without modifying source files.
- Hidden integration tests display no windows and can run unattended.
- Visible behavior is isolated in an unmistakably named opt-in project.
- Every acquisition has fault-injection coverage.
- CoreCLR and Native AOT use identical production contracts and ownership paths.
- Framework-dependent CoreCLR, self-contained CoreCLR, and Native AOT use isolated `obj`/`bin` trees and pass through their fixed projects in the complete unattended test scope.
- Publish directories exclude PDBs while separately retained symbols remain available for diagnostics.
- Default application-root creation and write probing fail clearly without silently choosing another location.
- Success, failure, close races, and renderer recovery finish with a zero resource ledger.
- Every milestone has a passing commit with its cumulative acceptance commands recorded in the commit or gate evidence.
