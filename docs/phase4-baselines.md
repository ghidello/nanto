# Phase 4 capability and plugin baselines

**Status:** Slice 0 baseline accepted on 2026-08-23.

Phase 4 measures capability compilation, static plugin composition, authorization lookup, lifecycle coordination, frontend module materialization, and runtime-selection costs against the completed Phase 3 product. Fixture packages are test-only build inputs and do not add production behavior during Slice 0.

## Diagnostic and evidence reservations

- Build diagnostics reserve `NANTO40xx` for capability documents, `NANTO41xx` for plugin manifests/catalogs, `NANTO42xx` for generated policy/registration conflicts, and `NANTO43xx` for runtime-compatibility selection.
- Runtime source-generated logging reserves event IDs `600–649` for authorization and `650–749` for plugin lifecycle. Existing `1–599` assignments do not change.
- `Nanto.Plugin.TestProtocol` schema v1 emits bounded symbolic plugin lifecycle transitions under the `NANTO_PLUGIN_LIFECYCLE ` prefix. It is repository-local test support, is not packable, and is not referenced by production assemblies.
- `Nanto.Plugin.Fixture.Filesystem` and `Nanto.Plugin.Fixture.CoreClrOnly` are packable test fixtures with no plugin behavior, manifests, native assets, or production registration during Slice 0.

## Reproduction

From the repository root on Windows x64:

```powershell
dotnet build
dotnet test -c Release
dotnet test -c Release -p:TestScope=All
```

The Release `All` lane retains the existing hidden WebView2, self-contained CoreCLR, and Native AOT deployment inspection. Manual visible and long-running projects remain filtered.

## Slice 0 observations

The baseline ran with .NET SDK `10.0.303` on Windows x64. The behavior-free fixture projects increased the evaluated solution graph to 36 projects. Four new protocol assertions raised the current Release counts without changing production behavior.

| Verification | Result |
| --- | ---: |
| Canonical Debug solution build | Passed, 0 warnings; 1m 39.50s |
| Canonical Debug default test scope | 442 passed, 0 failed; 45.289s |
| Release default test scope | 442 passed, 0 failed; 41.326s |
| Release `TestScope=All` | 529 passed, 0 failed; 2m 04.556s |
| Manual visible/long-running execution | Filtered; neither manual body executed |
| Filesystem fixture package | 3,889 bytes |
| CoreCLR-only fixture package | 3,915 bytes |

The deployment lanes retained their pre-plugin shapes:

| Mode | Shape | Observed bytes | Package bytes | Loader |
| --- | --- | ---: | ---: | --- |
| Framework-dependent CoreCLR contract build | Managed DLL launched through installed `dotnet`; 17 build-output files and separate local PDBs | 27,715,061 build-output bytes | Not packaged by this lane | Adjacent `WebView2Loader.dll` |
| Self-contained CoreCLR publish | Executable apphost plus managed/runtime files; 200 deployable files and no deployable PDB | 107,411,900 | 42,446,022 | Microsoft-signed adjacent `WebView2Loader.dll` |
| Native AOT publish | One executable and no deployable PDB, managed DLL, CoreCLR runtime, or dynamic WebView2 loader | 6,582,272 | 2,687,129 | Statically linked `WebView2LoaderStatic.lib` |

Both deployment publish lanes passed their three HostLifecycle, Navigation, and RendererRecovery smoke processes with zero final active resources. Detailed ignored machine evidence remains under `artifacts/phase1/evidence/deployment`; the two fixture packages are retained under `artifacts/phase4/slice0/packages` for local inspection only.
