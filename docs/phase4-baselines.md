# Phase 4 capability and plugin baselines

**Status:** Slice 0 baseline and Slice 1–2 checkpoints accepted through 2026-08-28.

Phase 4 measures capability compilation, static plugin composition, authorization lookup, lifecycle coordination, frontend module materialization, and runtime-selection costs against the completed Phase 3 product. Fixture packages are test-only build inputs and do not add production behavior during Slice 0.

## Diagnostic and evidence reservations

- Build diagnostics reserve `NANTO40xx` for capability documents, `NANTO41xx` for plugin manifests/catalogs, `NANTO42xx` for generated policy/registration conflicts, and `NANTO43xx` for runtime-compatibility selection.
- Runtime source-generated logging reserves event IDs `600–649` for authorization and `650–749` for plugin lifecycle. Existing `1–599` assignments do not change.
- `Nanto.Plugin.TestProtocol` schema v1 emits bounded symbolic plugin lifecycle transitions under the `NANTO_PLUGIN_LIFECYCLE ` prefix. It is repository-local test support, is not packable, and is not referenced by production assemblies.
- `Nanto.Plugin.Fixture.Filesystem` and `Nanto.Plugin.Fixture.CoreClrOnly` entered Slice 0 as empty packable fixtures with no production behavior. Slice 1 added their test-only manifests and build-transitive metadata without adding runtime registration.

## Slice 1 manifest and catalog checkpoint

**Status:** schema and package contract accepted on 2026-08-23.

- `Nanto.Sdk` packages the strict plugin-manifest schema v1 and discovers one manifest per selected plugin through package-owned
  `buildTransitive` items. The evaluated `project.assets.json` supplies exact package versions and direct dependency edges.
- Application bridge members now receive generated exact `app:<lowercase-symbolic-name>` constants. Invalid identifiers and
  lowercase collisions fail with `NANTO4200`/`NANTO4201`.
- Plugin manifests are bounded strict UTF-8 JSON. IDs, duplicate ownership, declared package files, path containment/reparse points,
  content hashes, exact CoreCLR dependency identity, missing/extra dependency edges, and cycles fail with stable `NANTO41xx` diagnostics.
- The SDK writes deterministic catalog and selection-snapshot JSON under the isolated intermediate tree. The snapshot fingerprints
  the assets file, selected manifests/catalog, target framework, configuration, hashed restore properties, MSBuild-reported input
  closure, applicable NuGet configs, lock file, and selected `global.json`. Mutation-free verification recomputes that fingerprint
  without writing and fails a missing or changed snapshot with `NANTO4115` instead of silently replacing it.
- The filesystem fixture owns `fixture.filesystem:read -> fixtureFilesystem.read`, a hashed path-like scope schema, and a hashed ESM
  module. The CoreCLR-only fixture names its exact package/version and bounded compatibility reason. Separate malformed and
  duplicate-ID packages prove package-boundary rejection.
- `Nanto.Plugin.PackageIntegrationTests` packs SDK and fixture packages into a local feed, restores isolated external consumers, and
  proves valid discovery plus `NANTO4104` malformed-manifest and `NANTO4108` duplicate-ID failures.

This checkpoint freezes the manifest schema, lowercase permission grammar, package asset layout, and catalog canonicalization. It
does not promote R-002 or R-003: frontend bundler materialization and runtime-selection artifact proof remain later slices.

| Slice 1 verification | Result |
| --- | ---: |
| Canonical Debug solution build | Passed, 0 warnings |
| Canonical Debug default test scope | 456 passed, 0 failed; 25.278s |
| Release `TestScope=All` | 544 passed, 0 failed; 1m 54.817s |
| Focused generator/manifest suite | 74 passed, 0 failed |
| Isolated package-consumer suite | 1 passed, 0 failed; valid, stale, malformed, and collision paths |

The Release gate executed neither manual test body. Its Native AOT deployment project passed in the final complete run. An earlier
parallel attempt exposed unrelated telemetry-listener interference and a transient WebView storage-lock cleanup failure; the
listener assertion was isolated to its own bounded tag set, package test builds were removed from the test body, and the complete
parallel lane then passed.

## Slice 2 capability compiler checkpoint

**Status:** schema and compiler contract accepted on 2026-08-28.

- `Nanto.Sdk` packages capability schema v1 and discovers `Capabilities/**/*.nanto-capability.json` by default while retaining
  explicit `NantoCapability` item control.
- The bounded strict-UTF-8 compiler validates document identity, stable `main` window selection, `local` or exact normalized HTTPS
  origins, permission ownership, duplicate grants, and plugin-owned scope schemas before emitting policy source.
- Generated policy entries and fingerprints are ordinal and deterministic. The inspection artifact exposes symbolic permissions,
  resolved application member IDs, and scope hashes without source paths or scope values.
- Contract generation writes policy outputs atomically and only when content changes. Isolated consumer evidence proves no-op
  timestamp stability, clean-build byte identity, and stale capability removal returning to an empty default-deny policy.
- Runtime origin resolution and bridge authorization remain Slice 3 work; this checkpoint adds no runtime JSON parsing or grants.

This checkpoint freezes capability schema v1, `local` semantics, exact-origin normalization, and duplicate-grant rejection under
D-078. Later changes to those contracts require an explicit compatibility decision.

| Slice 2 verification | Result |
| --- | ---: |
| Canonical Debug solution build | Passed, 0 warnings |
| Canonical Debug default test scope | 478 passed, 0 failed; 14.712s |
| Release `TestScope=All` | 566 passed, 0 failed; 1m 31.624s |
| Focused generator/capability suite | 96 passed, 0 failed |
| Isolated package-consumer suite | 1 passed, 0 failed; deterministic, no-op, redaction, and stale-removal paths |

The Release gate executed neither manual test body. The Phase 1 mixed-DPI visible follow-up remains open and is not claimed by this
checkpoint.

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
