# Phase 4 plan — capabilities and plugin foundation

**Status:** In progress. Slice 0 is complete and its accepted baseline is recorded in [`phase4-baselines.md`](phase4-baselines.md). Phase 1, Phase 2, and Phase 3 implementation are closed. The complete 550-process Phase 1 lifecycle/recovery soak passed on August 23, 2026. One separately approved Phase 1 follow-up remains open: the visible cross-monitor run on two active monitors with different effective DPI. Keep that item visible in the gate record and do not report it as passed.

Phase 4 turns the existing per-window list of generated command IDs into a compiled, inspectable authorization policy and establishes the static plugin composition and lifecycle model needed by the Windows MVP plugins in Phase 5. It does not ship the Phase 5 plugin catalogue.

## 1. Outcomes

At Phase 4 exit:

- applications author strict capability documents outside `nanto.json`;
- the SDK validates those documents against the application command catalog and referenced plugin manifests at build time;
- generated immutable tables select grants by window and exact top-level origin;
- application commands remain default-deny, including commands that are implemented and registered;
- local grants do not follow a WebView to a remote origin;
- a plugin NuGet package is the source of truth for its identity, permissions, scopes, frontend module, lifecycle registration, and runtime compatibility;
- plugin packages are selected statically through the evaluated NuGet graph and configured through generated typed C# APIs without runtime assembly scanning or a mandatory DI container;
- application- and window-scoped plugins start in generated dependency order and stop/dispose in reverse dependency order;
- partial startup, cancellation, repeated shutdown, and plugin callback failure release every acquired plugin resource;
- scope-aware plugins receive only compiled grants and revalidate requested native resources at their own native boundary;
- `native-aot` rejects a known incompatible plugin before publish and retains strict AOT warnings;
- `auto` selects CoreCLR only from verified static compatibility metadata, explains the exact cause, and records the selection outside the deployable directory;
- the generated frontend client and plugin modules remain ESM, framework-neutral, deterministic, and owned by the .NET build.

## 2. Scope boundaries

### In scope

- Capability schema v1 and a stable schema identity.
- Default SDK discovery of source-controlled capability documents.
- Build-time permission catalogs for application commands/events and referenced plugins.
- Exact window and origin selectors.
- Generated authorization tables and diagnostics.
- Plugin identity and manifest validation.
- Static plugin registration and explicit application composition.
- Application and primary-window plugin lifetimes.
- Typed scope access and plugin-owned native scope validation.
- Generated or package-supplied frontend plugin ESM modules.
- Native AOT/CoreCLR compatibility declarations, verification, plan output, and build evidence.
- Package-consumer tests using locally packed packages in an isolated directory.

### Explicitly out of scope

- Clipboard, dialogs, filesystem, opener, logging, system-information, or single-instance production plugins; those are Phase 5.
- Runtime plugin discovery, plugin folders, dynamic loading, runtime code emission, or independently versioned npm plugin packages.
- A general-purpose dependency-injection container or service-locator API.
- Invocation-scoped plugin instances. Phase 4 supports invocation-specific authorization data, not a new plugin instance per bridge call.
- Public multi-window creation, arbitrary window names, tray ownership, or shell integration. Phase 4 compiles a future-safe window dimension but exposes only the stable `main` selector.
- Wildcard permission identifiers, wildcard origins, inheritance, conditional grants, deny rules, or a policy expression language.
- Arbitrary filesystem glob semantics in core. Each plugin owns the syntax and validation of its scope values.
- Production remote-navigation allowlist design, CSP injection, installers, signing, updater, or application distribution packaging.
- A browser telemetry relay or changes to the Phase 3 telemetry decision.
- macOS, Linux, Arm64, WinUI, Windows App SDK, MAUI, WPF, or WinForms work.

## 3. Preserved invariants

- Portable public APIs contain no Win32, WebView2, WinUI, MAUI, WPF, or WinForms types.
- The Windows host owns the `HWND`; WebView2 work remains on its owning STA UI thread.
- Origin validation, protocol validation, session validation, and capability authorization remain separate checks in that order.
- Frontend content is untrusted. Selecting or configuring a plugin grants no frontend permission.
- The generated protocol, serializers, command IDs, manifest fingerprint, cancellation, streaming, event, and trace-context contracts remain shared by CoreCLR and Native AOT.
- The source generator and SDK emit deterministic output and never discover runtime registrations by scanning assemblies.
- Each plugin resource has one application or window owner. Teardown is cancellation-first, idempotent, bounded, and reverse dependency order.
- Strict AOT and trimming warnings are never suppressed or converted into a successful compatibility claim.
- Logging uses source-generated messages, stable event-ID ranges, opaque identifiers, and bounded symbolic values. Capability documents, scopes, origins, paths, arguments, and frontend data are not logged.
- `nanto.json` schema v1 remains the Phase 3 host/frontend orchestration contract. Capability authority is not duplicated into that file.

## 4. Starting point and known gaps

The Phase 2/3 implementation already provides:

- generated command/event descriptors and stable 32-bit IDs;
- a full manifest fingerprint and generated ESM client;
- explicit `NantoBridgeConfiguration` registration;
- generated `AppCapabilities` values selected through `WindowOptions.Capabilities`;
- a bridge session that rejects any ID absent from that list;
- exact development-origin checks and a session rotation on top-level navigation;
- strict `native-aot`, self-contained CoreCLR, and framework-dependent CoreCLR publish profiles;
- deterministic SDK tooling and an inspectable CLI plan.

Phase 4 replaces the author-facing `WindowOptions.Capabilities` list with compiled policy input. The low-level ID-set enforcement remains the final dispatch guard until the generated table can be passed directly to the session.

Slice 0 resolved the starting design mismatches:

- R-003 records the proposed metadata-driven change from Phase 3's `auto == native-aot` rule and remains awaiting Slice 10 proof;
- D-074 promotes R-007 using the completed Phase 3 implementation proof;
- O-008 and O-011 are closed by reference to D-059/D-062 and D-061 respectively;
- D-075 and D-076 record the capability document and exact-origin security boundaries; D-010 plus the refined R-002 retain static composition and NuGet-owned frontend modules as the Slice 9 proof target.

## 5. Capability vocabulary

The following terms are distinct:

- **bridge member** — one generated command or event with a numeric protocol ID;
- **permission** — a stable, human-authored identifier that expands to one or more bridge members;
- **grant** — one permission plus optional plugin-defined scope data;
- **capability document** — grants associated with window selectors and origin selectors;
- **compiled policy** — the immutable generated lookup table used by a host;
- **plugin manifest** — build-time metadata supplied by a plugin NuGet package;
- **plugin selection** — a plugin package present in the evaluated NuGet restore graph and therefore included in the static registry;
- **plugin registration** — the generated runtime adapter and factory contributed by a selected plugin package.

Permission identifiers use lowercase ASCII and the form `<namespace>:<name>`. The application namespace is `app`; plugin namespaces are their declared short IDs. Names consist of dot-separated lowercase ASCII segments. Identifiers are ordinal and case-sensitive after validation.

Each application bridge member receives a generated exact permission:

```text
projects.open     -> app:projects.open
projects.changed  -> app:projects.changed
```

A plugin manifest may map one permission to multiple bridge members. Phase 4 does not infer a `default` bundle and does not support wildcard expansion. A later package may deliberately declare a permission named `default`.

## 6. Capability document schema v1

Default project discovery is `Capabilities/**/*.nanto-capability.json`. Projects may remove or add `NantoCapability` MSBuild items explicitly. Documents are source-controlled build inputs and are not copied into the publish directory.

```json
{
  "$schema": "https://nanto.dev/schemas/capability/v1.json",
  "schemaVersion": 1,
  "identifier": "main-window",
  "windows": ["main"],
  "origins": ["local"],
  "permissions": [
    "app:projects.open",
    {
      "identifier": "fixture.filesystem:read",
      "scope": {
        "roots": ["$APPDATA/projects"]
      }
    }
  ]
}
```

Schema rules:

- unknown properties are errors at every level;
- `schemaVersion` must equal `1`;
- `identifier` is unique across the project, lowercase ASCII kebab-case, and diagnostic-only at runtime;
- `windows` is a non-empty, duplicate-free list; Phase 4 accepts only `main`;
- `origins` is a non-empty, duplicate-free list containing `local` or exact normalized HTTPS origins;
- an exact origin contains scheme, IDN-normalized host, and effective port only; user info, path, query, fragment, wildcard, opaque URI, and non-HTTP(S) schemes are rejected;
- `local` resolves after application content validation to the production application origin or configured development origin;
- an explicit origin must use HTTPS; HTTP is accepted only through `local` when it resolves to the validated development origin, so a plaintext remote document can never receive native grants;
- `permissions` is non-empty and duplicate-free after canonicalization;
- a string grants an unscoped permission;
- an object contains exactly `identifier` and `scope`;
- a scoped permission must declare a scope schema in its plugin manifest; an unscoped permission rejects `scope`;
- two documents may contribute different permissions to the same window/origin pair; duplicate grants for the same permission are errors rather than implicit merges;
- every identifier must resolve to exactly one permission catalog entry.

There are no wildcard grants, deny entries, environment substitutions, secrets, or implicit development grants. Development and production use the same documents; only `local` resolves to the active trusted content origin.

## 7. Compiler and generated policy

`Nanto.Sdk` remains the orchestration owner. Before C# compilation it will:

1. collect application bridge metadata, plugin manifests, and `@(NantoCapability)` files;
2. validate schemas, encodings, sizes, identities, and duplicates;
3. build the complete permission catalog;
4. resolve each permission to bridge member IDs and a scope compiler;
5. canonicalize window/origin selectors and scopes;
6. emit C# source for the immutable policy;
7. emit a deterministic policy fingerprint and redacted inspection file under `obj`;
8. include the generated source in `CoreCompile`.

The generated policy is sorted by window selector, origin selector, permission ID, and member ID using ordinal ordering. It contains no source paths and performs no JSON parsing at runtime. Scope payloads are emitted through plugin-generated typed factories or immutable literals; they are not deserialized through reflection.

The application contract exposes the generated policy through one generated registration call. Empty policy input compiles successfully and grants nothing. A command or event that exists in the bridge catalog but has no grant remains unavailable.

Required compiler diagnostics cover:

- unreadable, invalid UTF-8, oversized, malformed, or wrong-version documents;
- duplicate document identifiers, selectors, and grants;
- unknown windows, origins, permissions, bridge members, or plugin manifests;
- invalid permission/plugin identifiers;
- scope on an unscoped permission, missing scope on a scoped permission, and plugin-specific scope errors;
- plugin manifest/catalog fingerprint mismatch;
- generated C# identifier or numeric member-ID collision;
- nondeterministic or conflicting plugin contributions.

Diagnostics name a project-relative capability file and JSON property path. They never print scope values.

## 8. Runtime authorization

Runtime selection follows this order for every inbound message:

1. WebView event-source extraction succeeds.
2. The event source and current top-level WebView source normalize to the same exact origin.
3. The current navigation generation has a live bridge session.
4. The protocol envelope, version, manifest, and session token validate.
5. The compiled policy resolves the current window selector and exact origin.
6. The requested command/event ID is present in that grant set.
7. A scope-aware handler validates the requested native operation against its typed scope.

Any navigation rotates and disposes the current session before a new one can accept messages. `local` grants resolve only to the validated content origin. Navigating to another origin therefore produces an empty grant set unless that exact origin is explicitly present in a capability document. Returning to local content creates a fresh session; no request, stream, or subscription survives navigation.

Phase 4 keeps the existing bounded transport error `commandUnavailable` for unknown and unauthorized commands so the frontend cannot enumerate installed native functionality. Scope rejection uses the same public error unless the plugin command declares a typed application-domain error. Diagnostic logs record only bounded stage and opaque member/plugin IDs.

## 9. Scope contract

Core supplies a small portable contract; it does not interpret plugin scope values.

- A generated permission descriptor owns a typed, immutable scope value.
- `NantoCommandContext` exposes the already-authorized permission ID and an internal/generated typed scope accessor.
- A plugin command adapter must invoke its native-boundary validator before touching the OS, filesystem, process, network, clipboard, or other protected resource.
- Validation uses the actual normalized native target, not the frontend-provided alias alone.
- The validator is deterministic, cancellation-aware where work is required, and returns a bounded authorization result.
- Scope compilation rejects ambiguous aliases and normalizes values once; the native validator still defends against traversal, link, case, canonicalization, and time-of-check/time-of-use concerns appropriate to that resource.

The Phase 4 fixture proves a path-like scope without declaring that fixture syntax to be the eventual filesystem plugin contract.

## 10. Plugin package contract

A plugin is a NuGet package. It may contain:

- portable public APIs and implementations;
- generated bridge registration code;
- one package manifest;
- permission and scope definitions;
- generated frontend ESM source/declarations;
- build-transitive items that expose those assets to `Nanto.Sdk`;
- platform-specific assemblies or native assets;
- compatibility metadata.

The package manifest has a versioned strict schema and includes:

```json
{
  "schemaVersion": 1,
  "id": "fixture.filesystem",
  "runtimeCompatibility": "nativeAot",
  "dependencies": [],
  "permissions": [
    {
      "identifier": "fixture.filesystem:read",
      "members": ["fixtureFilesystem.read"],
      "scopeSchema": "scopes/read-v1.schema.json"
    }
  ],
  "frontendModule": "frontend/index.ts"
}
```

The exact schema is finalized in Slice 1. Its invariants are fixed now:

- one manifest per plugin package and one globally unique plugin ID;
- no manifest scripts or executable discovery hooks;
- all paths are package-relative and remain inside the package root;
- all contributions are bounded and hashed;
- permission/member mappings are complete and deterministic;
- compatibility declarations include a bounded reason and exact package/dependency identity when CoreCLR is required;
- the SDK verifies declared files and catalog fingerprints rather than trusting labels alone.

The evaluated restore graph is the selection authority: referencing a Nanto plugin package selects its manifest, registry adapter, compatibility metadata, and frontend module for that build. Transitive Nanto plugin dependencies are selected too. Plugin manifests declare their Nanto-plugin dependency edges; the SDK verifies those edges against the evaluated NuGet graph and rejects missing, extra, or cyclic plugin dependencies. The plugin may expose a generated typed configuration API, but configuration does not control whether the plugin is selected. Selection and configuration grant no frontend permission.

Both the CLI and SDK consume the same selection snapshot that an SDK restore/evaluation target emits from the host project's current `obj/project.assets.json` plus package manifests. A snapshot records the host project, target framework, requested build configuration, relevant restore global properties, the assets-file and package-manifest hashes, and the complete evaluated restore-input closure. That closure includes every imported project/props/targets file reported by MSBuild, applicable `NuGet.Config` and lock files, central package inputs, the selected SDK from `global.json`, and any other input that can change restore evaluation. `--plan` performs a mutation-free MSBuild evaluation, compares that closure and the assets-file fingerprint with the snapshot, and fails with an actionable `restore required` diagnostic when no exact match exists; it never restores or builds. The executing build performs its normal restore/build first, then reloads the resulting selection snapshot before choosing a publish runtime. Plugin selection must not vary by `NantoBuildMode`; the SDK compares the contract-build and publish snapshots and fails on divergence. The snapshot fingerprint is carried into the generated registry and build evidence so a stale or different graph cannot be used silently.

## 11. Static composition API

Phase 4 adds a small `NantoPluginConfiguration` to `NantoApplicationOptions`. The SDK emits a static registry from selected plugin manifests. A package may generate a strongly typed configuration extension for its options while its generated factory retains instance creation:

```csharp
var plugins = new NantoPluginConfiguration();
plugins.ConfigureFixtureFilesystem(options);

var application = new NantoApplicationOptions
{
    ApplicationId = "com.example.app",
    Content = content,
    Bridge = bridge,
    Plugins = plugins,
    PrimaryWindow = new WindowOptions { Title = "Example" },
};
```

The generated registry supplies stable identity, dependency order, permission catalog, bridge contributions, scope validators, compatibility metadata, and factories for supported lifetimes. Selected-plugin dependency edges come from validated plugin manifests; unrelated plugins use ordinal plugin ID as the stable topological tie-breaker. `CaptureSnapshot()` validates that configuration targets a selected plugin, rejects missing required configuration, and freezes typed configuration before host startup. A selected plugin that needs no options requires no C# call. There is no `Assembly.GetTypes`, attribute scan, dynamic proxy, service provider, or reflection-based activation.

Configuration values remain application-owned inputs. Plugin factories receive only their generated typed configuration and narrow lifecycle contexts rather than a general service container.

## 12. Plugin lifecycle and ownership

Supported lifetimes are application and window. A plugin may contribute either or both through separate generated adapters.

Lifecycle phases are:

```text
Configure -> Start -> Running -> Stop -> Dispose
```

Semantics:

- `Configure` is synchronous, side-effect constrained, and runs once in generated dependency order while options are being frozen. It may contribute generated bridge registrations and validate configuration but must not acquire native resources.
- `StartAsync` runs once in generated dependency order with a cancellation token and a narrow context.
- `Running` is a framework state, not a callback.
- `StopAsync` is cancellation-first and runs once in reverse dependency order for every plugin whose start completed.
- `DisposeAsync` runs once in reverse dependency order for every created plugin instance, including an instance whose start failed, unless an earlier callback for that same instance timed out and may still be executing.
- repeated or concurrent shutdown shares one completion task;
- startup failure preserves the original exception and aggregates bounded cleanup failures without skipping remaining cleanup;
- each lifecycle context owns a revocable lease over every framework service it exposes, including window access and dispatcher scheduling;
- plugin callbacks receive the remaining application shutdown budget; on timeout Nanto atomically revokes that lease, rejects new context/dispatcher work, detaches framework-owned subscriptions, observes the abandoned task, and only then continues host teardown;
- after a callback times out, Nanto invokes no later lifecycle callback concurrently on that instance; it marks the instance abandoned, continues cleanup of other instances, and reports that the abandoned instance was not disposed;
- a non-cooperative timed-out plugin is reported as a contract violation. Nanto guarantees that it can no longer reach host-owned window/WebView resources, but does not falsely report the plugin's private resources as disposed;
- application plugins start before the primary window is created and stop after all windows have stopped;
- window plugins are owned by the window, start only after the portable window exists, and stop before bridge/WebView/controller/`HWND` teardown;
- UI access is explicit through `IUiDispatcher`; ordinary callbacks are thread-agnostic and must not assume a synchronization context.

Lifecycle contexts expose only portable identifiers, logging, cancellation, and explicitly supported services. A future platform escape hatch requires a concrete plugin need and separate ownership decision.

## 13. Frontend module contract

The plugin package remains the sole version source. Its build produces or packages ESM source that the consuming `Nanto.Sdk` copies into the generated client tree under a stable package-derived path. Consumers do not install a matching npm package.

The SDK verifies:

- plugin ID and permission/member catalog fingerprint;
- frontend module path containment;
- deterministic content and write-if-different behavior;
- no collision with `@nanto/core`, `@nanto/app`, or another plugin module;
- TypeScript compilation in the React/Vite and vanilla TypeScript/Vite fixtures;
- removal of stale plugin modules after a package reference is removed.

The module delegates transport to `@nanto/core` and does not gain access to capability documents or scope contents. Authorization remains native and default-deny.

## 14. Runtime compatibility and selection

Compatibility has two declared values:

```csharp
public enum RuntimeCompatibility
{
    NativeAot,
    CoreClr,
}
```

Selection rules:

- `native-aot` is strict. A statically selected CoreCLR-only plugin fails before publish with plugin ID, package ID, incompatible dependency identity, and bounded reason. AOT/trimming warnings from otherwise compatible code remain build failures.
- `coreclr` and `coreclr-framework-dependent` are explicit and retain their Phase 3 meanings.
- `auto` selects self-contained CoreCLR only when verified static metadata for a selected plugin requires it. Otherwise it selects Native AOT.
- an unexpected Native AOT publish failure under `auto` fails the build; it is never caught and retried as CoreCLR.
- package metadata alone is insufficient proof of Native AOT compatibility. The strict publish lane and analyzer output remain authoritative.
- a CoreCLR-only declaration must name the exact dependency and reason. Generic `not compatible` metadata is rejected.

`dotnet nanto build --plan` reads the current restore-graph selection snapshot and shows requested runtime, selected runtime, selected plugin IDs, the snapshot fingerprint, and bounded selection reasons without executing any plan step. If the snapshot is absent or stale, planning fails instead of guessing. Build output repeats the selection prominently. A deterministic `<output>.nanto-build.json` evidence file is written beside, not inside, the deployable directory and contains no absolute paths. Artifact inspection verifies that the selected runtime and snapshot fingerprint match the generated registry and directory shape.

## 15. Diagnostics

Add stable diagnostics for:

- capability discovery, validation, and compilation;
- plugin manifest discovery and validation;
- duplicate or unresolved contributions;
- plugin configure/start/stop/dispose transitions;
- authorization rejection stage;
- requested and selected runtime plus compatibility reason;
- frontend module generation and stale-output removal.

Generator/SDK errors use stable `NANTO4xxx` codes and project-relative file/property locations: `NANTO40xx` for capability documents, `NANTO41xx` for plugin manifests/catalogs, `NANTO42xx` for generated policy/registration conflicts, and `NANTO43xx` for compatibility selection. Runtime source-generated logs reserve authorization `600–649` and plugin lifecycle `650–749` without renumbering existing application/window/dispatcher/WebView/assets/teardown events. No diagnostic emits titles, routes, absolute paths, origins, scopes, command arguments, frontend data, or file contents.

## 16. Repository boundaries

Expected ownership:

| Area | Responsibility |
| --- | --- |
| `Nanto.Core` | Portable permission, compiled-policy, plugin configuration, lifecycle, compatibility, and scope contracts. |
| `Nanto.Generators` | Application permission catalog and static plugin adapters visible from compilation symbols. |
| `Nanto.Sdk` | Capability/plugin manifest validation, policy generation, frontend module materialization, and MSBuild inputs/outputs. |
| `Nanto.Hosting.Windows` | Exact WebView/window/origin binding and window-plugin placement in native teardown. |
| `Nanto.Cli` | Plan rendering, runtime selection, compatibility diagnostics, and build evidence. |
| fixture plugin packages | Prove packaging, scopes, static composition, lifecycle, ESM generation, and AOT/CoreCLR metadata without becoming product plugins. |
| tests | Compiler goldens, lifecycle/failure matrices, package-consumer builds, hidden WebView authorization, and deployment gates. |

Do not create a production plugin catalogue project in Phase 4.

## 17. Delivery sequence

Each slice ends with focused tests, `dotnet build`, and the default `dotnet test` when the solution graph changes. Run `dotnet test -c Release -p:TestScope=All` at decision checkpoints and the final gate. Manual visible and long-running Phase 1 tests remain separately approved.

### Slice 0 — baseline, decisions, and fixture protocol

**Status:** Complete. The accepted counts, artifact shapes, diagnostic reservations, test protocol, and behavior-free package fixtures are recorded in [`phase4-baselines.md`](phase4-baselines.md).

Deliver:

- record capability-schema and exact-origin decisions, and refine static-composition and `auto` recommendations with explicit proof checkpoints;
- close stale O-008/O-011 entries by reference to Phase 2 decisions;
- promote R-007 using Phase 3 plan evidence;
- define stable diagnostic ranges and test protocol additions;
- add empty fixture packages/projects without production behavior;
- capture current fast/Release counts and Native AOT/CoreCLR artifact shapes.

Exit: documentation and empty fixtures build without changing runtime behavior.

### Slice 1 — plugin manifest and permission catalog

Deliver:

- strict plugin manifest schema v1;
- build-transitive `NantoPluginManifest` discovery;
- restore-graph selection snapshots, input-freshness checks, plugin dependency validation, and deterministic topological order;
- path containment, size, encoding, ID, duplicate, hash, and compatibility validation;
- generated application exact permissions;
- deterministic merged permission catalog and inspection output;
- malformed-package and collision fixtures.

Exit: every catalog entry has one owner and exact member mapping; selected plugins and dependency order are reproducible from the current restore graph without runtime activation.

### Slice 2 — capability schema and compiler

Deliver:

- capability schema v1 and default MSBuild glob;
- strict JSON and semantic validation;
- exact origin normalization and `local` token representation;
- plugin scope-schema validation;
- deterministic generated C# policy and fingerprint;
- incremental/write-if-different and stale-output cleanup.

Exit: valid fixtures compile identically across clean builds; every malformed class has a stable diagnostic.

### Slice 3 — policy integration and API migration

Deliver:

- portable compiled-policy contracts;
- generated policy registration in application options;
- bridge snapshot validation against the permission catalog;
- migration of templates, samples, and TestApp from `WindowOptions.Capabilities`;
- obsolete/remove the old author-facing list only after all consumers migrate;
- default-deny tests for empty, missing, and partial policies.

Exit: implemented-but-ungranted commands/events are unavailable in CoreCLR unit tests and existing protocol behavior is otherwise unchanged.

### Slice 4 — exact-origin WebView enforcement

Deliver:

- bind each navigation generation to normalized top-level origin and compiled grants;
- compare WebMessage event source with current WebView source;
- rotate/dispose sessions on every navigation start and teardown;
- resolve `local` against production and development origins;
- allow explicitly granted HTTPS remote origins without transferring local grants;
- hidden WebView tests for local, remote, return navigation, stale messages, streams, events, malformed origins, and wrong WebView identity.

Exit: remote navigation has zero local permissions by default and an explicit HTTPS remote grant enables only its listed members.

### Slice 5 — typed scopes and native-boundary validation

Deliver:

- typed compiled-scope descriptor/access contract;
- generated scope factory and plugin validator adapter;
- a path-like fixture validator that resolves aliases then rechecks the normalized target;
- tests for traversal, case/canonicalization, malformed aliases, scope mismatch, and time-of-check boundary behavior;
- bounded public errors and redacted diagnostics.

Exit: changing frontend arguments cannot broaden a compiled grant, and bypassing the fixture validator is structurally impossible through its generated command adapter.

### Slice 6 — static plugin composition

Deliver:

- `NantoPluginConfiguration` and frozen snapshot;
- generated strongly typed plugin configuration registration;
- duplicate identity and registration/catalog mismatch checks;
- plugin bridge contribution without runtime scanning;
- fixture plugin selection through package reference and typed configuration in TestApp;
- dependency-boundary tests rejecting reflection/dynamic-loading mechanisms.

Exit: the runtime registry contains exactly the plugins in the fingerprinted selection snapshot, and none receives a frontend grant implicitly.

### Slice 7 — application plugin lifecycle

Deliver:

- configure/start/running/stop/dispose coordinator;
- dependency-order startup and reverse-dependency-order cleanup;
- cancellation-first bounded shutdown using the application budget;
- partial-start, throw, cancel, timeout, repeated-close, and concurrent-close coverage;
- structured lifecycle diagnostics.

Exit: exhaustive cooperative failures leave zero application-plugin owners and preserve the primary failure; a non-cooperative timeout revokes every framework lease and is recorded honestly as a plugin contract violation.

### Slice 8 — window plugin lifecycle

Deliver:

- primary-window plugin factories and portable window context;
- placement in Windows window/WebView acquisition and teardown;
- stop-before-bridge/WebView/controller/`HWND` dependency assertions;
- UI-dispatcher boundary tests;
- partial native initialization and renderer-failure coverage.

Exit: completed window-plugin callbacks stop/dispose before their native prerequisites disappear; a timed-out callback loses all framework access before those prerequisites disappear.

### Slice 9 — frontend plugin module packaging

Deliver:

- package-generated ESM/declaration assets;
- consumer SDK copy, fingerprint validation, stable import paths, and stale removal;
- React/Vite and vanilla TypeScript/Vite type-check fixtures;
- package add/remove/update incremental tests;
- isolated locally packed NuGet consumer test.

Exit: one NuGet reference plus optional typed C# configuration supplies matching native and frontend contracts without an npm plugin package.

### Slice 10 — compatibility verification and runtime selection

Deliver:

- `RuntimeCompatibility` metadata in generated registrations;
- Native-AOT-compatible and CoreCLR-only fixture plugins;
- pre-publish strict rejection and exact dependency diagnostics;
- `auto` resolution from the fingerprinted selected-plugin snapshot;
- plan/human/JSON rendering and adjacent deterministic build evidence;
- artifact-shape verification and no-fallback-on-unexpected-AOT-failure tests.

Exit: strict AOT identifies the exact blocker; `auto` selection is predictable before execution and unambiguous afterward.

### Slice 11 — templates, docs, and package-consumer ergonomics

Deliver:

- template capability document and explicit plugin example comments;
- capability and plugin authoring documentation;
- schema packaging and stable `$schema` identities;
- migration guide from generated `AppCapabilities` lists;
- redacted `--plan` examples;
- clean external-consumer builds in paths with spaces and Unicode.

Exit: a new template runs with only its declared permission and a custom SPA consumes a fixture plugin module without framework-specific runtime changes.

### Slice 12 — hardening and Phase 4 gate

Deliver:

- full permission/plugin failure matrix;
- deterministic clean/rebuild/incremental/package evidence;
- CoreCLR framework-dependent, CoreCLR self-contained, and Native AOT deployment checks;
- repeated process smoke runs with plugin lifecycle ledger evidence;
- size/startup/build-time comparison against the Phase 3 baseline;
- `phase4-gate.md` with automated and any separately approved live evidence;
- architecture decision and roadmap status updates only after exit criteria pass.

Exit: the acceptance checklist below passes with no unexplained AOT/trimming warnings and no leaked plugin/native/process resources.

## 18. Test inventory

Prefer extending existing fast projects when ownership is clear. Add projects only for a distinct execution/package boundary:

- `Nanto.Core.Tests`: compiled policy, scope context, lifecycle coordinator, ordering, failure, cancellation, and concurrency.
- `Nanto.Generators.Tests`: application permissions, static registrations, diagnostics, deterministic output, and collisions.
- `Nanto.Cli.Tests`: runtime resolution, plan output, redaction, build evidence, and unexpected AOT failure behavior.
- `Nanto.Hosting.Windows.Tests`: origin normalization/session binding and native teardown ordering with fakes.
- `Nanto.Hosting.Windows.HiddenIntegrationTests`: real WebView navigation/origin/authorization behavior.
- existing self-contained and Native AOT deployment projects: selected compatible fixture plugin and artifact inspection.
- one package-consumer integration project: locally pack core/generator/SDK/CLI plus fixture plugins, restore in isolation, generate frontend modules, and publish both selected modes.

Test dimensions:

| Dimension | Required cases |
| --- | --- |
| Policy | empty, exact grant, partial grant, duplicate, unknown, malformed, deterministic merge |
| Origin | production local, development local, explicit HTTPS remote, rejected plaintext remote, ungranted remote, navigation away/back, stale source/session |
| Bridge | unary, stream, event, cancel, subscribe/unsubscribe, manifest mismatch, wrong WebView identity |
| Scope | absent, valid, malformed, alias escape, normalized target escape, plugin rejection |
| Registry | zero, one, several, duplicate ID, direct/transitive selection, configured/unconfigured, selected but ungranted |
| Lifecycle | configure/start/stop/dispose failure at every index, dependency order, cancellation, cooperative timeout, non-cooperative lease revocation, repeated/concurrent shutdown |
| Runtime | explicit AOT, explicit CoreCLR modes, auto-compatible, auto-CoreCLR requirement, undeclared AOT failure |
| Tooling | clean, incremental no-op, package add/remove/update, stale output, spaces, Unicode |

`TestScope=All` must continue to execute no visible or long-running manual project.

## 19. Measurements and regression limits

Before closing Phase 4, record:

- clean and no-op incremental build time with 0, 1, and 10 fixture plugins;
- capability compilation time for 1, 10, and 100 documents;
- generated policy/source/frontend byte counts;
- application startup delta with 0, 1, and 10 no-op application/window plugins;
- shutdown delta and failure count across at least 100 repeated plugin-host smoke processes;
- Native AOT executable and self-contained CoreCLR directory deltas;
- allocations for authorization lookup and scope retrieval on the steady-state bridge path.

The gate sets accepted budgets from measured fixtures before declaring completion. The steady-state authorization lookup must perform no JSON parsing, filesystem access, assembly scanning, or unbounded allocation.

## 20. Acceptance checklist

- [ ] Capability schema v1 and plugin manifest schema v1 are strict, packaged, documented, and deterministic.
- [ ] Unknown permissions, windows, origins, scopes, and plugin contributions fail at build time with stable diagnostics.
- [ ] Empty or absent grants expose no commands or events.
- [ ] Implemented but ungranted commands cannot be invoked.
- [ ] Navigating remotely removes local grants, cancels active work, and disposes subscriptions.
- [ ] Explicit remote grants require HTTPS and apply only to the exact normalized origin and listed members; HTTP is available only through a validated `local` development origin.
- [ ] The current WebView/window/origin/session identity is checked independently of member authorization.
- [ ] Scope-aware fixture operations revalidate normalized native targets at their boundary.
- [ ] Selecting or configuring a plugin never implicitly grants it.
- [ ] Runtime plugin discovery, dynamic activation, and mandatory DI are absent.
- [ ] Application and window plugins start in generated dependency order and stop/dispose in reverse dependency order.
- [ ] Every cooperative startup/shutdown failure releases all previously acquired plugin resources; non-cooperative timeout evidence proves lease revocation and does not claim private plugin cleanup.
- [ ] Cooperative window plugins stop before bridge/WebView/controller/`HWND` teardown; a timed-out plugin's framework lease is revoked before that teardown starts.
- [ ] Generated plugin ESM works in React/Vite and vanilla TypeScript/Vite without an npm plugin package.
- [ ] Strict Native AOT fails early with the exact incompatible plugin/dependency and no warning suppression.
- [ ] `auto` selects CoreCLR only from verified static metadata and never retries after an unexpected AOT failure.
- [ ] Human, JSON, plan, and adjacent artifact evidence agree on requested and selected runtime.
- [ ] Framework-dependent CoreCLR, self-contained CoreCLR, and Native AOT retain isolated outputs and the same production contracts.
- [ ] `dotnet build`, `dotnet test`, and `dotnet test -c Release -p:TestScope=All` pass.
- [ ] No unexplained `IL2026`, `IL3050`, `IL3052`, analyzer, trim, or AOT warnings remain.
- [ ] No PDB enters a production publish directory; symbols remain separate.
- [ ] Final size, build, authorization, startup, shutdown, and repeated-process evidence is recorded in `phase4-gate.md`.
- [ ] The open mixed-DPI Phase 1 follow-up remains visible and is not reported as passed.

## 21. Decision checkpoints

Record a decision before crossing each checkpoint:

1. **After Slice 1:** freeze plugin manifest schema v1, permission identifier grammar, package asset layout, and catalog fingerprint.
2. **After Slice 2:** freeze capability schema v1, `local` semantics, exact-origin normalization, and duplicate-grant behavior.
3. **After Slice 5:** accept or revise the typed scope boundary using the path-like fixture evidence.
4. **After Slice 8:** accept or revise application/window lifecycle APIs using failure-injection, AOT size, and ergonomics evidence; resolve R-005.
5. **After Slice 9:** accept or revise NuGet-owned frontend module packaging using external-consumer and bundler evidence; resolve R-002.
6. **After Slice 10:** accept or revise metadata-driven `auto` selection using artifact evidence; resolve R-003.

Schema or public-lifecycle changes after their checkpoint require an explicit compatibility decision, not incidental refactoring.

## 22. Completion rule

Phase 4 closes only when capability documents compile to deterministic exact window/origin authorization, local grants are lost on remote navigation, scope-aware native operations cannot exceed compiled grants, plugin composition is static and selected from the evaluated restore graph, application/window lifecycle failure matrices prove reverse-dependency cleanup and safe lease revocation, NuGet-owned frontend modules pass both maintained frontend fixtures, and runtime selection is predictable and evidenced across strict Native AOT and CoreCLR.

Phase 4 completion does not close the mixed-DPI Phase 1 follow-up. It remains a separate gate record until executed on two active monitors with different effective DPI.
