# Phase 3 plan — CLI, framework-neutral SPA integration, and development loop

**Status:** Implementation candidate. Slices 0–9 have working code and automated coverage; Slice 10 remains open for the live developer-loop, custom-SPA/pnpm, Aspire-runtime, and final repeated-measurement evidence listed in [`phase3-gate.md`](phase3-gate.md).

Phase 3 turns the completed Windows host and generated bridge into a usable development product. It adds project creation, inspectable build orchestration, a framework-neutral frontend development contract, predictable Hot Reload or restart behavior, standard telemetry, and optional Aspire orchestration.

The complete 550-process lifecycle/recovery soak and the visible mixed-DPI run on two suitable monitors remain open Phase 1 acceptance follow-ups. They do not block Phase 3, are not waived, and must not be reported as passed.

## 1. Outcomes

At Phase 3 exit, a developer can run:

```powershell
dotnet new install Nanto.Templates
dotnet new nanto -n MyApp --frontend react
cd MyApp
dotnet nanto dev
dotnet nanto build --runtime native-aot
```

and receive:

- a raw-Win32/WebView2 application using the Phase 1 lifecycle and deployment paths;
- the Phase 2 generated C# registry, JSON metadata, authorization, and ESM client under both CoreCLR and Native AOT;
- frontend HMR or live reload owned by the selected frontend tool;
- managed Hot Reload when supported and an explained host restart otherwise;
- deterministic process ownership and cleanup on normal exit, Ctrl+C, startup failure, and child-process failure;
- an inspectable build plan and actionable environment diagnostics;
- standard logs, metrics, and traces without requiring an exporter;
- optional Aspire orchestration and OTLP export without changing application command implementations.

An existing non-React SPA must be attachable through configuration alone. Adding support for another frontend framework or bundler must not require changes to `Nanto.Core`, `Nanto.Hosting.Windows`, or the bridge protocol.

## 2. Scope boundaries

### In scope

- `Nanto.Cli` as a .NET tool exposing `dev`, `build`, and `doctor`.
- `Nanto.Templates` with React/Vite and custom-frontend paths.
- One schema-versioned, strictly validated `nanto.json` configuration model.
- A plan engine shared by human output, JSON output, validation, and execution.
- Exact development-server origin support in the portable content contract and Windows host.
- Frontend command execution, URL readiness, managed-host startup, Hot Reload/restart observation, and deterministic process-tree cleanup.
- Generated TypeScript output written into a frontend-watched directory without unnecessary timestamp changes.
- Production frontend build plus CoreCLR and Native AOT publish orchestration.
- Standard `ActivitySource`, `Meter`, and existing structured logging integration.
- W3C Trace Context propagation across the generated frontend bridge.
- Optional Aspire resource composition and an end-to-end sample with one dependent service.
- Measured developer-loop baselines and non-regression evidence.

### Explicitly out of scope

- Plugins or a plugin SDK.
- Phase 4 capability documents, wildcard grants, or a new permission language. Phase 3 templates continue using generated `AppCapabilities` values in C#.
- Multi-window behavior beyond preserving the existing primary-window contract.
- Installers, signing, update feeds, store packaging, runtime bundling, or Phase 5 distribution policy.
- A polished component library or application UI.
- macOS, Linux, Android, iOS, or Arm64 hosts.
- A production browser-telemetry relay. Phase 3 proves trace propagation and development export; a packaged relay remains behind its separate design decision.
- Silent Native AOT fallback. Until plugin compatibility evidence exists, `auto` resolves to Native AOT and fails honestly on incompatibility.

## 3. Non-negotiable invariants

- Development uses the same generated registry, serializers, capabilities, origin checks, session rotation, and host contracts as production.
- The CLI never grants bridge access. The application still supplies generated capabilities explicitly.
- A development server is trusted only when the application opts into development content and the source origin exactly matches the normalized configured origin.
- Arbitrary external navigation may display according to host policy but never inherits bridge authority.
- MSBuild remains the only writer of generated C# and TypeScript contracts. The CLI invokes and observes the build; it does not implement a second generator.
- Commands, working directories, resolved tools, runtime mode, inputs, outputs, and dependencies are visible in the plan before execution.
- Process teardown is cancellation-first, bounded, idempotent, and reverse dependency order. The native host stops before the frontend server it depends on.
- No command payloads, frontend data, application identifiers, titles, routes, absolute paths, secrets, or file contents enter Nanto telemetry.
- `dotnet nanto dev` requires neither Aspire nor an OTLP collector.
- The CLI, SDK, templates, telemetry integration, and Aspire adapter remain separate from portable runtime contracts.

## 4. Starting point

Phase 2 already provides:

- `Nanto.Sdk.targets`, which runs the SDK emitter before `CoreCompile`;
- generated `NantoGeneratedJsonContext.g.cs` and `@nanto/app/index.ts` output;
- stable semantic discovery shared by C# and TypeScript generation;
- framework-dependent CoreCLR, self-contained CoreCLR, and Native AOT build isolation;
- a real WebView2 bridge with exact production-origin authorization;
- a committed frontend workspace proving the generated runtime API.

At the start of Phase 3, the repository did not yet have:

- a public configuration schema or loader;
- a CLI or execution-plan model;
- a development content/origin contract;
- process supervision or readiness probing;
- templates or a standalone sample;
- live generated-client placement in an application frontend;
- `ActivitySource`, `Meter`, W3C bridge propagation, or Aspire resources.

## 5. Proposed repository boundaries

The exact assembly names are locked in Slice 1, but responsibilities must remain separated as follows:

| Project/package | Responsibility |
| --- | --- |
| `Nanto.Core` | Portable content-source contract, bridge trace metadata, runtime activities and metrics |
| `Nanto.Hosting.Windows` | Exact dev-origin navigation/message enforcement and Windows host instrumentation |
| `Nanto.Generators` | Existing reflection-free dispatcher and manifest generation |
| `Nanto.Sdk` | MSBuild entry points, contract emission, atomic generated-file publication, publish properties |
| `Nanto.Tooling` or equivalent internal assembly | Configuration, plan, tool discovery, readiness, process-independent orchestration models shared by CLI tests |
| `Nanto.Cli` | .NET tool command surface, console/JSON presentation, cancellation, process supervision |
| `Nanto.Templates` | `dotnet new nanto` content and template symbols |
| `Nanto.Hosting.Aspire` | Optional Aspire resource extension; no dependency from core, host, SDK, or CLI |
| `@nanto/core` | Existing browser protocol runtime plus optional trace-context hooks, with no OpenTelemetry dependency |
| `@nanto/app` | Existing generated application client |

Do not create a shared tooling assembly merely to move a few methods. Introduce it only when both the CLI and SDK genuinely need the same versioned configuration or plan model; otherwise keep the model in `Nanto.Cli` and expose a narrow SDK invocation contract.

## 6. User-facing command contract

### `dotnet nanto dev`

```text
dotnet nanto dev [--config <path>] [--configuration Debug]
                 [--environment Development] [--no-hot-reload]
                 [--plan] [--format human|json]
```

Required behavior:

1. Locate one `nanto.json` from the current directory or an explicit `--config` path.
2. Validate configuration and resolve all relative paths against the configuration directory.
3. Validate the .NET SDK, WebView2 Runtime, frontend executable, package-manager state, and configured port.
4. Restore frontend dependencies when the package manifest/lockfile stamp or installed dependency tree is absent or stale.
5. Run the contract-generation build and expose the generated ESM client at its configured frontend path.
6. Start the configured frontend command in its configured working directory.
7. Wait for the exact development URL to respond within the configured deadline; do not use an arbitrary sleep.
8. Start the host through the selected CoreCLR watch strategy and pass the normalized development URL through an explicit development-only channel.
9. Forward child output with stable resource names and preserve the original child text.
10. Stop the host/watch tree first, then the frontend tree, on Ctrl+C, child failure, or CLI failure.

The first Ctrl+C requests graceful cancellation. A repeated interrupt may force bounded containment teardown. Exit code `0` means all owned processes exited and cleanup completed; a forced kill or residue produces a nonzero code and names only the affected resource and bounded operation.

### `dotnet nanto build`

```text
dotnet nanto build [--config <path>] [--configuration Release]
                   [--runtime auto|native-aot|coreclr|coreclr-framework-dependent]
                   [--output <directory>] [--plan] [--format human|json]
```

Execution order:

1. Validate configuration and tools.
2. Restore/generate contracts.
3. Run the configured frontend production command.
4. Verify that `frontend.build.dist` exists, is inside the configured frontend root unless explicitly allowed, and contains the declared entry asset.
5. Build the production asset manifest through SDK targets.
6. Publish the host using an isolated `NantoBuildMode`.
7. Report publish directory, runtime profile, symbols location, frontend asset size, executable size, and total installed bytes.

Phase 3 build produces a publish directory and evidence report, not an installer. `auto` prints that it selected Native AOT. It must never catch an AOT failure and silently republish with CoreCLR.

`--plan` performs no restore, build, command execution, directory creation, or file mutation. Human output is optimized for review; `--format json` emits a versioned machine-readable document used by tests and future integrations.

### `dotnet nanto doctor`

```text
dotnet nanto doctor [--config <path>] [--format human|json]
```

Doctor is read-only. It reports:

- selected configuration and schema version;
- pinned and active .NET SDK;
- Windows/x64 support status and WebView2 Runtime availability;
- resolved host project and runtime profile;
- resolved frontend executable/package manager and lockfile state;
- normalized dev URL and whether its port is free, already served externally, or occupied unexpectedly;
- generated/output directory existence and permission indicators without printing absolute paths in ordinary logs;
- actionable remediation commands.

Phase 3 does not add an automatic `doctor --fix`; mutation and dependency installation occur only as visible plan steps of commands that require them.

## 7. Configuration v1

Slice 1 must resolve open decision O-007 and commit a JSON schema before templates stabilize. The candidate shape is:

```json
{
  "$schema": "https://nanto.dev/schemas/config/v1.json",
  "schemaVersion": 1,
  "application": {
    "identifier": "com.example.myapp",
    "productName": "MyApp",
    "hostProject": "MyApp.csproj"
  },
  "frontend": {
    "directory": "Frontend",
    "generatedClient": "src/generated/nanto",
    "install": {
      "file": "npm",
      "arguments": ["ci"]
    },
    "dev": {
      "file": "npm",
      "arguments": ["run", "dev", "--", "--host", "127.0.0.1", "--port", "5173", "--strictPort"],
      "url": "http://127.0.0.1:5173",
      "readyTimeoutSeconds": 60
    },
    "build": {
      "file": "npm",
      "arguments": ["run", "build"],
      "dist": "dist"
    }
  },
  "build": {
    "runtime": "auto"
  }
}
```

Configuration rules:

- Unknown properties, duplicate properties, unsupported schema versions, invalid URI forms, invalid runtime values, and escaping paths are errors.
- Commands are executable plus argument arrays, not implicit shell strings. General shell expressions may be added later only as an explicit, visibly riskier form.
- Command working directories default to `frontend.directory`; paths are normalized once and retained as structured values.
- Executable resolution follows the current process environment and Windows executable rules. Native executables launch directly. On Windows, a resolved `.cmd` or `.bat` package-manager shim launches through `%ComSpec% /d /s /c` using one centralized, tested quoting routine that preserves the configured executable and argument boundaries; arbitrary command text is never accepted or concatenated. The execution plan identifies this adapter explicitly. An alternative package-manager adapter may resolve the shim to its Node entry point and launch `node` directly when that mapping is deterministic.
- The managed host project and frontend directory must remain inside the application root by default.
- The dev URL contains only `http` or `https`, no user information, and a concrete host and port. Templates bind loopback by default.
- Configuration values never contain secrets. A later environment map may name variables to forward, but the plan and logs show names, not values.
- Environment overlays use `nanto.<Environment>.json`, merge objects recursively, replace arrays, and receive the same strict validation. Phase 3 initially supports `Development` and explicit user-selected names; there is no implicit production secrets file.
- Frontend restore stamps live under an ignored Nanto intermediate directory and contain only hashes of package manifests, lockfiles, the install command, and package-manager version. Restore is skipped only when the stamp and installed dependency tree both exist and match.
- MSBuild remains the source of compilation and publish properties. The config selects commands, paths, runtime intent, and development behavior without duplicating arbitrary MSBuild properties.
- Runtime application identity/window ownership must have one source. Slice 1 must either generate the small runtime metadata surface from `nanto.json` or deliberately keep it in C# and remove the duplicate fields from schema v1. It must not ship both as independently editable values.

Capability documents, plugin configuration, signing, bundling, and multi-window arrays remain reserved for their roadmap phases even if the schema namespace anticipates future versions.

The architecture document's broad configuration sketch includes windows, capability documents, plugins, bundling, and signing. Schema v1 must not expose placeholder fields for those later phases. Slice 1 records the staged boundary in the decision register so the narrower implemented schema does not silently contradict that longer-term model.

## 8. Inspectable plan model

Every CLI command first creates an immutable plan. Execution consumes that same plan; presentation must not reconstruct it independently.

A plan contains:

- schema version and command kind;
- configuration and application roots represented by stable logical labels;
- resolved runtime profile and explanation;
- ordered steps with stable IDs, dependencies, working-directory labels, executable, argument list, timeout policy, inputs, outputs, and mutation classification;
- environment variable names added or removed, with values redacted;
- readiness probes;
- expected long-running resources and shutdown order;
- final artifact categories.

Stable initial step IDs should include:

```text
config.validate
tools.validate
frontend.restore
contracts.generate
frontend.start
frontend.ready
host.watch
frontend.build
assets.validate
host.publish
artifacts.inspect
shutdown.host
shutdown.frontend
```

The JSON plan schema is versioned independently from `nanto.json`. Tests snapshot both human and JSON forms. Absolute machine paths may appear only in explicitly requested diagnostic JSON fields and must never leak into normal structured logs or telemetry.

## 9. Development content and security

The current application contract assumes production assets at `https://app.nanto.invalid`. Phase 3 introduces a portable discriminated content source with two modes:

- production assets backed by `IWebAssetProvider` plus a root-relative initial route;
- an explicit development-server start URI and normalized trusted origin.

Exact type names are finalized in Slice 2. The model must make invalid states unrepresentable rather than combining nullable `Assets`, `DevelopmentUrl`, and `InitialRoute` properties.

Windows-host requirements:

- Prepare and lease assets only in production-content mode.
- Navigate directly to the configured development start URI in development mode.
- Bind WebMessage acceptance to the exact normalized scheme, host, and effective port of the selected content source.
- Permit bridge traffic from neither sibling subdomains nor alternate loopback names, ports, frames, opaque origins, redirects, or arbitrary external pages.
- Rotate the protocol session on top-level navigation exactly as in production.
- A redirect away from the trusted dev origin may display according to navigation policy but receives no bridge session or capabilities.
- Returning to the configured origin requires a new handshake.
- Development mode must be explicit in application startup and visible in diagnostics. A production publish must not silently honor a development URL from ambient environment alone.
- HMR WebSockets and frontend subresources remain owned by the browser/dev server; Nanto does not proxy or interpret bundler traffic.

Real-WebView tests cover exact-origin success, host alias mismatch, port mismatch, iframe mismatch, redirect away/back, navigation-session rotation, and production-mode refusal of development overrides.

## 10. Process and readiness model

Each process has one owner and a small state machine:

```text
Created → Starting → Running → Stopping → Exited
                    ↘ Faulted ↗
```

The CLI owns the frontend command and managed watch command. On Windows, all descendants join a kill-on-close Job Object or an equivalently strong contained process group. Graceful shutdown is attempted first; containment is the final guarantee.

Rules:

- Capture stdout and stderr asynchronously without allowing a blocked reader to deadlock shutdown.
- Bound retained diagnostic lines and lengths; stream output live rather than buffering whole builds.
- Never invoke user commands synchronously from the WebView STA thread.
- If a required one-shot step fails, do not start dependents.
- If the frontend exits before host startup, fail startup and clean completed resources.
- If the frontend exits while running, stop the host and return the frontend exit code category.
- If the host exits normally, stop the frontend and return success only when all cleanup succeeds.
- If readiness times out, include the logical URL, elapsed duration, frontend exit state, and reproduction command without dumping response content.
- For a CLI-managed server, a reachable URL before process start is an occupied-port error. If no dev command is configured, the URL is treated as an explicitly external server and must already be reachable.
- Readiness uses bounded HTTP probes with cancellation. Any HTTP response proves server reachability; TLS, DNS, connection, and timeout failures remain distinct bounded categories.
- A first interrupt requests graceful stop. A repeated interrupt or expired shutdown deadline closes containment and returns a distinct nonzero exit code.

Fast tests use deterministic fake processes, streams, clocks, and HTTP handlers. Integration tests launch real process trees that spawn grandchildren, ignore graceful termination, flood output, exit early, and retain files so containment and cleanup are proven rather than inferred.

## 11. Generated-client live loop

MSBuild remains the single generator owner. Phase 3 extends `Nanto.Sdk` so that:

- `nanto.json` selects the frontend generated-client directory through the validated SDK invocation contract;
- generated C# stays under the project intermediate tree;
- generated TypeScript is written atomically and only when content changes;
- unchanged builds do not alter timestamps or trigger frontend watcher work;
- stale temporary files are recovered deterministically;
- concurrent build attempts cannot interleave partial generated output;
- source paths in documentation remain project-relative;
- errors retain source diagnostics and prevent the host/frontend from running against mismatched contracts.

The React/Vite template maps `@nanto/app` to the configured generated directory and excludes generated files from source control. A contract edit must produce this observable sequence:

```text
C# edit → MSBuild/generator → atomic @nanto/app update → frontend type check/HMR
        → managed Hot Reload or explained restart → protocol re-handshake
```

Tests assert that a DTO/member change updates the generated client once, an unrelated C# body edit does not touch it, a breaking type change reaches the frontend compiler, and no partial file is observable while the frontend watcher reads.

## 12. Production asset handoff

The Phase 1 production default remains embedded, versioned assets. Phase 3 replaces the TestApp's manually maintained resource list and manifest with deterministic SDK targets driven by `frontend.build.dist`.

After the frontend command succeeds, the SDK must:

1. Resolve the distribution root beneath the configured frontend directory.
2. Reject missing roots, reparse points/symlinks that escape the root, reserved Nanto paths, case-insensitive path collisions, invalid normalized paths, and files that change during hashing.
3. Enumerate regular files in stable ordinal path order.
4. Compute byte length and SHA-256 for each file.
5. Emit the strict `nanto-assets.json` document and deterministic `EmbeddedResource` items with stable logical names.
6. Feed those resources to `VersionedWebAssetProvider` through one generated/template bootstrap contract.
7. Record frontend asset bytes and hashes in build evidence without logging asset paths during runtime.

The frontend build and asset collection execute before publish and only once per plan. A direct `dotnet publish` of a generated application must invoke the same SDK validation or fail with a clear instruction; the CLI cannot be the only path that produces correct embedded assets. Incremental builds may reuse evidence only when the complete input fingerprint matches.

Tests cover deterministic ordering, content changes, empty/missing output, case collisions, Unicode normalization, reserved paths, symlinks/reparse points, concurrent mutation, spaces, large files, and single-file/Native AOT resource loading. Directory-backed assets remain an explicit development or externally managed option, not the template's production default.

## 13. Managed watch strategy

The default development host is framework-dependent CoreCLR under the pinned SDK. Native AOT remains a build/CI path.

Slice 4 must prototype the supported `dotnet watch` integration before locking behavior. The selected approach must:

- use documented SDK behavior rather than parsing localized console prose as a control protocol;
- preserve method-body Hot Reload where the SDK supports it;
- detect process restart and assign a stable Nanto reason category such as initial start, incompatible managed change, build failure recovery, or explicit user restart;
- surface build failures while keeping the frontend server alive;
- re-establish the bridge session after restart;
- keep every host descendant inside CLI containment;
- support `--no-hot-reload` as a deterministic restart-only diagnostic mode.

If the SDK exposes no stable machine-readable reason for a restart, Nanto reports the honest bounded category `managed-watch-restart` and forwards the SDK explanation verbatim as child output. It must not infer a more specific cause from unstable text.

## 14. Templates and frontend conformance

### React/Vite template

The reference template contains:

- one Windows host project with Native AOT analyzers enabled;
- a small generated bridge API showing success, expected failure, cancellation, an event, and a stream;
- minimal explicit capability grants;
- React, TypeScript, Vite, a pinned lockfile, and the generated `@nanto/app` alias;
- `nanto.json` with executable/argument-array commands;
- production embedded assets and development-server selection through the same application startup contract;
- no Aspire dependency in the default template.

The generated repository includes a local .NET tool manifest pinning the matching `Nanto.Cli` version. The template post action restores that tool when approved, which makes the documented immediate `dotnet nanto dev` command work. If post actions are disabled or offline, creation succeeds but prints the exact `dotnet tool restore` command; Nanto must not pretend the CLI is available. Global tool installation remains an explicit alternative, not hidden machine mutation.

Template, CLI, SDK, runtime, generated browser core, and schema compatibility are versioned deliberately. Template integration tests install all artifacts from one isolated local feed and reject incompatible CLI/config schema pairs with an actionable message.

### Custom frontend path

`--frontend custom` creates the host, configuration, generated-client directory convention, and clear attachment instructions without creating a JavaScript framework. The developer supplies commands, URL, and dist path. No runtime code changes are required to attach an existing SPA.

### Conformance fixtures

Phase 3 tests:

- React/Vite as the reference path;
- one materially different real SPA toolchain selected and pinned after a small comparison spike;
- npm and pnpm lockfile/command paths;
- an external pre-started dev server with no managed frontend command;
- repository paths containing spaces and non-ASCII characters;
- fixed occupied ports and readiness timeouts;
- frontend commands that fail before and after readiness;
- production asset directories with missing, escaping, symlinked, or stale output.

The second toolchain must be genuine; the deterministic test HTTP server remains useful for failure tests but does not satisfy the toolchain conformance criterion.

## 15. Diagnostics and exit codes

Human output uses stable resource labels such as `config`, `frontend`, `host`, `build`, and `doctor`. JSON output uses one versioned event per line so long-running commands remain streamable.

Initial exit-code categories:

| Code | Category |
| ---: | --- |
| 0 | Success and complete cleanup |
| 2 | CLI usage or unsupported option |
| 3 | Configuration validation |
| 4 | Missing/incompatible tool or runtime |
| 5 | Frontend restore/build/start/readiness failure |
| 6 | Managed build/watch/publish failure |
| 7 | Artifact validation failure |
| 8 | Cleanup or forced-containment failure |
| 9 | Internal CLI failure |

Each failure includes a stable symbolic code, failed plan-step ID, bounded explanation, and direct reproduction command where safe. Exceptions and stack traces are shown only under an explicit diagnostic option; secrets and frontend response bodies remain redacted.

## 16. Telemetry and trace propagation

### Runtime primitives

Core and the Windows host add standard instrumentation with no exporter dependency:

- one documented `ActivitySource` namespace for application/window startup, bridge calls, navigation/recovery, and shutdown;
- one documented `Meter` namespace for startup duration, bridge duration/outcomes, active streams/subscriptions, renderer failures, restarts, and shutdown residue;
- the existing source-generated `ILoggerMessage` events for bounded operational detail.

Instrumentation is cheap when no listener exists. Tag names and values are bounded and symbolic. Tests verify that payloads, DTO values, application IDs, titles, routes, filesystem paths, and exception messages do not appear.

### W3C bridge context

Protocol v1 gains optional, bounded `traceparent` and `tracestate` metadata without changing command authorization. The browser core accepts an optional trace-context provider/observer abstraction and does not depend on OpenTelemetry JavaScript. The native session validates W3C syntax and size before creating the server activity; invalid metadata is ignored or rejected according to the protocol decision recorded in the slice, never passed through unchecked.

The generated client API remains unchanged for ordinary callers. An optional frontend telemetry adapter can install hooks and prove:

```text
frontend activity → native bridge activity → application HTTP/dependent-service activity
```

Cancellation, application failure, authorization denial, malformed input, and unexpected failure receive bounded activity status and tags without recording command arguments.

### Export policy

Applications own OpenTelemetry SDK registration and exporters. A small optional package or sample may provide conventional registration helpers, but `Nanto.Core`, `Nanto.Hosting.Windows`, generated clients, templates, and ordinary `dotnet nanto dev` do not require an OTLP endpoint.

Direct browser OTLP/HTTP is development-only evidence with explicit CSP and CORS configuration. The packaged host-relay design remains deferred until its security, batching, credential, rate-limit, shutdown-flush, and Native AOT costs are proven separately.

## 17. Optional Aspire integration

Aspire work begins only after the direct CLI loop is green. `Nanto.Hosting.Aspire` is optional and depends outward on Aspire; no production Nanto package depends on it.

The initial `AddNantoApp(...)` resource extension should compose, without hiding:

- the managed Nanto host project or executable;
- the configured frontend command/resource and endpoint;
- readiness ordering so the host starts after the frontend is reachable;
- dependent-service references and environment variables;
- OTLP environment supplied through standard Aspire conventions;
- reverse-order shutdown.

The effective Aspire resource graph must remain inspectable. The sample includes one dependent HTTP service and proves a frontend-triggered generated command creates a native child activity whose outgoing request reaches that service under the same trace.

Aspire is tested at three layers:

1. fast resource-model tests for names, references, endpoints, and ordering;
2. unattended integration tests for startup, readiness, telemetry correlation, Ctrl+C, and process cleanup where the required local prerequisites are available;
3. a separately opted-in dashboard observation only if visual evidence is required. Such a scenario must not silently enter `dotnet test -p:TestScope=All`.

The implementation records O-013 after the resource prototype. Browser package/export choices remain under O-014 until the telemetry prototype supplies evidence.

## 18. Delivery sequence

Each slice must leave `dotnet build` and `dotnet test` green. Relevant integration coverage lands with the behavior, not in a later cleanup milestone.

### Slice 0 — baseline and fixture protocol

Implementation:

- Add `docs/phase3-baselines.md` with machine/tool versions and measurement procedure.
- Add minimal fixture applications used by later CLI tests without presenting them as templates.
- Define machine-readable lifecycle markers for frontend ready, host started, generated client updated, host restarted, and cleanup complete.
- Measure the existing direct build/generation path before adding orchestration.

Exit:

- Cold and warm measurement commands are reproducible.
- Baselines record median and p95 rather than a single best run.
- Functional timeouts are clearly separated from performance budgets.

### Slice 1 — configuration and plan engine

Implementation:

- Resolve O-007 and commit config v1 plus JSON schema.
- Add strict source-generated JSON parsing and path/URI normalization.
- Implement immutable plan steps and human/JSON rendering.
- Implement `dev --plan`, `build --plan`, and `doctor` without child execution.
- Decide CLI parsing dependency with version, size, and maintenance evidence; otherwise keep a small explicit parser.

Tests:

- Golden valid configurations and every invalid/ambiguous field category.
- Overlay precedence, array replacement, unknown keys, duplicate keys, path escape, spaces, Unicode, and redaction.
- Plans are deterministic across working directories and do not mutate the filesystem.

Exit:

- A developer can inspect every intended command and output before it runs.
- Config/runtime ownership has no duplicate source of truth.

### Slice 2 — development content origin

Implementation:

- Add the portable production/development content-source union.
- Update option validation and Windows startup without weakening production assets.
- Generalize navigation and WebMessage checks to the selected exact trusted origin.
- Preserve session rotation and default-deny capabilities.

Tests:

- Core option/state tests, Windows fast origin tests, and real hidden WebView2 origin/navigation cases.
- CoreCLR and Native AOT continue using production assets unchanged.

Exit:

- A configured local dev origin can use the generated bridge.
- Every other origin remains unable to invoke commands.

### Slice 3 — process supervisor and readiness

Implementation:

- Add contained child-process ownership, bounded output forwarding, readiness probes, cancellation, and exit mapping.
- Implement frontend restore/start/ready execution from the immutable plan.
- Add Ctrl+C and repeated-interrupt behavior.

Tests:

- Fake deterministic unit tests plus real grandchildren, early exit, ignored shutdown, output flood, occupied port, timeout, and locked-file integration cases.

Exit:

- No owned child or locked fixture file remains after success or injected failure.

### Slice 4 — managed development loop

Implementation:

- Complete the `dotnet watch` prototype and lock the supported integration.
- Start the CoreCLR host after frontend readiness.
- Report initial start, build failure, Hot Reload, restart, and exit categories honestly.
- Keep frontend running through recoverable managed build failures.

Tests:

- Method-body edit, incompatible shape edit, compile error/recovery, host crash, frontend crash, rapid Ctrl+C, and repeated restart.

Exit:

- Supported edits Hot Reload; incompatible edits restart and re-handshake.
- Cleanup remains deterministic across every transition.

### Slice 5 — live generated bindings

Implementation:

- Make SDK output selection config-driven through one validated invocation contract.
- Add atomic write-if-changed behavior and single-writer protection.
- Wire the reference frontend alias and watcher.

Tests:

- Contract change, unrelated body change, frontend type error, concurrent build, interrupted write, and source-location preservation.

Exit:

- Contract edits reach frontend type checking/HMR without manual copying or host/frontend protocol drift.

### Slice 6 — production build and doctor

Implementation:

- Execute frontend build, deterministic embedded-asset collection, isolated runtime publish, and artifact inspection.
- Implement `auto`, strict Native AOT, self-contained CoreCLR, and framework-dependent CoreCLR resolution.
- Finish doctor prerequisites and remediation output.

Tests:

- Plan/execution agreement, missing dist, stale output, escaping path, frontend failure, strict AOT warning failure, spaces, and runtime-mode isolation.
- Deployment smoke continues to cover the same generated contracts under CoreCLR and Native AOT.

Exit:

- `build --plan` predicts the executed graph.
- Build output is product-shaped, contains no PDBs, and records separate symbols/evidence.

### Slice 7 — templates and toolchain conformance

Implementation:

- Package `Nanto.Cli` as the `dotnet nanto` tool.
- Package `Nanto.Templates` with `react` and `custom` symbols.
- Add the pinned local tool manifest and template post-action/fallback instructions.
- Add dependency restore behavior and actionable offline/missing-tool failures.
- Select and pin the second real frontend toolchain.

Tests:

- Install packages into isolated local feeds/hives.
- Create, restore, dev-run, modify, build, and launch generated applications from paths with spaces.
- Verify npm, pnpm, Vite, the second toolchain, and custom external-server mode.

Exit:

- The documented new-project sequence works from a clean test environment.
- An existing SPA attaches without runtime changes.

### Slice 8 — runtime telemetry and W3C propagation

Implementation:

- Add ActivitySource/Meter instruments and bounded semantic tags.
- Extend protocol/client hooks for W3C context.
- Add optional conventional OpenTelemetry registration in samples or a separate package.

Tests:

- Parent/child propagation, malformed metadata, redaction, cancellation/failure status, metric bounds, disabled-listener behavior, and strict Native AOT publish.

Exit:

- Frontend-to-native-to-dependent-service trace correlation is proven without changing application command code.
- No-listener runtime and size deltas are recorded and accepted.

### Slice 9 — optional Aspire composition

Implementation:

- Prototype and record O-013.
- Add `Nanto.Hosting.Aspire`, sample AppHost, frontend resource, and dependent service.
- Wire standard OTLP environment and health/readiness relationships.

Tests:

- Resource graph, endpoint/reference propagation, startup order, correlated telemetry, Ctrl+C, and cleanup.

Exit:

- Direct CLI development remains unchanged and dependency-free.
- Aspire mode shows resource health, logs, traces, and metrics through standard mechanisms.

### Slice 10 — hardening and Phase 3 gate

Implementation:

- Run the complete security, lifecycle, dev-loop, telemetry, deployment, template, and toolchain matrix.
- Record final baselines, artifact sizes, runtime versions, known limitations, and accepted budgets.
- Add `docs/phase3-gate.md` and update the roadmap decision register.

Exit:

- Every acceptance item below has committed automated evidence or an explicitly named, approved manual follow-up.
- Neither open Phase 1 follow-up is silently folded into Phase 3 status.

## 19. Test projects and scope

Expected test boundaries:

| Project | Default scope | Purpose |
| --- | --- | --- |
| `Nanto.Cli.Tests` | Fast | Config, plans, discovery, readiness, output, exit codes, fake processes |
| `Nanto.Sdk.Tests` or existing generator tests | Fast | Atomic outputs, path selection, no-op generation, deterministic plans |
| `Nanto.Hosting.Windows.Tests` | Fast | Dev-origin policy and instrumentation without visible UI |
| `Nanto.DevLoop.IntegrationTests` | Integration | Real process trees, watch/restart, Vite/custom server, cleanup |
| `Nanto.Template.IntegrationTests` | Integration | Local tool/template install, creation, restore, build, launch |
| `Nanto.Telemetry.Tests` | Fast | Activities, metrics, redaction, and propagation without external processes |
| `Nanto.Telemetry.IntegrationTests` | Integration | OTLP fixture and process-boundary propagation |
| `Nanto.Hosting.Aspire.Tests` | Fast | Resource-model construction without launching an AppHost |
| `Nanto.Hosting.Aspire.IntegrationTests` | Integration | Optional orchestration and lifecycle coverage |

Continue using `TestScope=Fast|Integration|All`; scope selects inventory only and never changes runtime or compiler semantics. Manual dashboard or visible-browser evidence, if needed, receives a separate `manual=true` project and explicit command. It must not execute from the solution-level `All` scope.

Tests that launch Node, `dotnet watch`, or template processes use contained process groups and unique temporary application identities. Successful runs remove their artifacts; the first failure retains a bounded report under `artifacts/phase3/**`.

## 20. Developer-loop measurements

Slice 0 records the measurement environment and initial values. The gate tracks at least:

| Measure | Start | End |
| --- | --- | --- |
| Cold first run | CLI invocation to frontend ready and host bridge ready | Both readiness markers observed |
| Warm first run | Same, with restored dependencies and warm build outputs | Both readiness markers observed |
| Frontend feedback | Saved frontend file | Fixture confirms HMR/live-reload update |
| Contract feedback | Saved C# contract/DTO | Generated file updated and frontend type check completed |
| Managed Hot Reload | Saved supported method-body edit | New behavior observed in the same host process |
| Managed restart | Saved incompatible edit | New host process ready and bridge re-handshaken |
| Shutdown | First Ctrl+C | All owned descendants exited and files unlocked |
| Plan generation | CLI invocation with `--plan` | Human/JSON plan emitted with no writes |

Use at least one cold run and 30 warmed iterations; record median, p95, failures, SDK/runtime, Node/package-manager, WebView2, CPU, storage, and power mode. CI functional timeouts are not performance budgets. Phase 3 sets accepted budgets from measured fixtures before the corresponding feature is declared complete, and later phases may change them only with recorded evidence.

## 21. Acceptance checklist

- [x] `dotnet new nanto --frontend react` creates a buildable reference application.
- [ ] `--frontend custom` attaches an arbitrary SPA through configuration without runtime changes.
- [ ] `dotnet nanto dev` validates, restores, generates, starts, waits, watches, reports, and cleans up deterministically.
- [ ] Frontend HMR/live reload occurs without restarting the native host.
- [ ] Supported C# edits Hot Reload; incompatible edits restart with an honest reason category.
- [ ] Contract edits atomically update `@nanto/app` and trigger frontend type checking.
- [x] `dotnet nanto build --plan` is deterministic, complete, redacted, and mutation-free.
- [ ] Native AOT, self-contained CoreCLR, and framework-dependent CoreCLR builds remain isolated and use identical production contracts.
- [ ] `dotnet nanto doctor` reports every required prerequisite with actionable repair guidance.
- [ ] Exit, Ctrl+C, startup failure, child failure, and repeated interrupt leave no child processes or locked files.
- [ ] Paths with spaces and Unicode, npm and pnpm, occupied ports, external dev servers, and two real frontend toolchains pass.
- [x] Development bridge authority is limited to the exact configured origin and rotates on navigation/restart.
- [x] Activities, metrics, and logs are bounded, redacted, optional, and use standard .NET/OpenTelemetry concepts.
- [ ] W3C context correlates frontend, native command, and dependent-service work.
- [x] Strict Native AOT publish has no unexplained trimming or AOT warnings after instrumentation.
- [x] Ordinary development requires neither Aspire nor an OTLP collector.
- [ ] Optional Aspire orchestration exposes inspectable resources, readiness, logs, traces, metrics, and clean shutdown.
- [ ] Final measured developer-loop and size evidence is recorded in the Phase 3 gate.

## 22. Decision checkpoints

The following choices must be proved and recorded before their public surfaces stabilize:

1. **Configuration ownership (O-007):** exact v1 fields, runtime metadata source, overlay rules, and schema publication path.
2. **CLI parsing dependency:** dependency versus a small parser, with package size, maintenance, and behavior evidence.
3. **CLI distribution/bootstrap:** local tool manifest, template post action, offline behavior, and version-alignment rules.
4. **Managed watch integration:** documented way to observe Hot Reload/restart without treating console prose as a protocol.
5. **Second frontend toolchain:** selection based on meaningful architectural difference, deterministic CI behavior, and maintenance cost.
6. **Telemetry hook shape:** W3C propagation without an OpenTelemetry dependency in `@nanto/core` or generated clients.
7. **Aspire API (O-013):** smallest inspectable `AddNantoApp(...)` resource surface after the prototype.
8. **Browser telemetry (O-014):** direct development export evidence and explicit deferral or acceptance of any package surface.

Every checkpoint updates the architecture decision register. A prototype may be discarded; temporary APIs do not become compatibility commitments merely because they were useful in a test fixture.

## 23. Completion rule

Phase 3 closes only when the React/Vite reference path and custom-SPA path pass the same generated bridge, authorization, lifecycle, process-cleanup, CoreCLR, and Native AOT contracts; the build graph is inspectable; telemetry is optional and correlated; and the recorded developer-loop budgets are met.

The two open Phase 1 manual follow-ups remain visible in their own gate record throughout Phase 3. Completing Phase 3 neither waives nor implicitly passes them.
