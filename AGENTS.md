# AGENTS.md

## Project

Nanto is an early-stage .NET framework for small native applications with web frontends. The committed MVP target is Windows, using raw Win32, the Evergreen WebView2 Runtime, and Native AOT by default.

The current source of truth is [`docs/Nanto-architecture-and-roadmap.md`](docs/Nanto-architecture-and-roadmap.md). Read the relevant sections before making architectural changes. If implementation and documentation disagree, call out the mismatch rather than silently changing direction.

## Current priority

Work from the roadmap in order. Milestones 1–4 are complete; Milestone 5 in `docs/phase1-plan.md` is the current acceptance target. Its client-size/DPI, multi-monitor, and native-appearance batches are complete; renderer/process recovery is the current review batch, followed by diagnostics/acceptance. Keep virtual-host mapping and the existing Milestone 4 asset/navigation guarantees. Nanto must remain self-contained and must not reference or copy experimental assemblies. Avoid templates, plugins, DI infrastructure, multi-window support, packaging, polished UI, and future-platform work during this phase.

## Engineering constraints

- Keep portable public APIs free of Win32, WebView2, WinUI, MAUI, WPF, WinForms, and other platform types. Put explicit platform escape hatches behind optional interfaces.
- On Windows, Nanto owns the `HWND`; WebView2 attaches to it. Do not introduce WinUI 3 or Windows App SDK dependencies.
- Prefer generated, pinned interop definitions (not hand-copied signatures or raw OS magic numbers), and use `DisableRuntimeMarshalling=true` for the AOT path.
- Generate commands, serializers, plugin registries, capabilities, and TypeScript bindings at compile time. Avoid runtime assembly scanning, dynamic proxies, runtime code emission, and unbounded reflection.
- Treat frontend content as untrusted. Command access is default-deny and must be checked against the calling origin and window/WebView identity.
- Give every native handle, COM object, subscription, cancellation source, and process one clear owner. Teardown must be deterministic, idempotent, cancellation-first, and reverse dependency order. Do not force unrelated application-, window-, and thread-scoped resources into one artificial global stack.
- Keep WebView2 work on the STA UI thread. Never synchronously block on an operation whose completion requires that thread.
- Keep appearance application/profile-scoped. Apply `System`, `Light`, or `Dark` through the platform WebView profile and standard `prefers-color-scheme`; do not add a theme-specific frontend protocol or a Nanto-owned preference store.
- Treat public window sizes as client-area DIPs. The Windows UI thread is Per-Monitor-V2 aware and owns native placement and DPI. Phase 1 lets Windows choose initial placement and does not expose programmatic X/Y until a real display model exists.
- On display or work-area changes, enumerate current monitor work areas and move a normal window only when it has no positive intersection with any of them. Preserve partially visible placement and native size; align an oversized window to the nearest work-area origin. Use `GetWindowPlacement`/`SetWindowPlacement` to preserve minimized and maximized state while Windows corrects the normal restore placement. Windows reports hidden windows as `SW_SHOWNORMAL`; correct their current rectangle through non-showing `SetWindowPos` so they remain hidden.
- Resolve Windows `System` appearance through the documented `UISettings` color API and `ColorValuesChanged`; do not depend on the undocumented `AppsUseLightTheme` registry value.
- Report WebView2 process failures through the portable renderer event. Attempt at most one automatic main-renderer reload per window lifetime; close normally when that recovery cannot start, its navigation fails, or another main-renderer failure occurs. Browser-process loss closes without reload, while subframe and automatically recreated child-process failures remain diagnostic-only.
- Keep frontend integration framework-neutral: a development command and URL, a production build command and asset directory, and generated ESM bindings. Vite is a reference adapter, not a core dependency.
- Development may use CoreCLR, but it must exercise the same generated registries, serialization, authorization, lifecycle, and host contracts as Native AOT.
- Use standard .NET, npm, OpenTelemetry, and optional Aspire concepts instead of opaque project-specific machinery.

## Change discipline

- Make the smallest change that advances the current roadmap phase.
- Preserve the separation between core contracts, platform hosts, generators, SDK/build tooling, CLI, tests, and plugins described in the architecture document.
- Keep new dependencies minimal, pin versions, and explain their AOT, trimming, size, and native-runtime impact.
- Do not suppress AOT or trimming warnings without documenting the exact reason. Strict builds should have no unexplained `IL2026`, `IL3050`, `IL3052`, or equivalent warnings.
- Update the architecture/decision register when a change proves, rejects, or materially revises a documented decision.
- State platform-specific behavior honestly; do not force lowest-common-denominator semantics into portable APIs.

## C# style

Treat `.editorconfig` as the source of truth for formatting and naming. Apply the same naming, member-ordering, and formatting rules to C# examples in documentation and design proposals so examples can become production code without a style rewrite. Use modern C# features supported by the pinned SDK when they make code clearer, but do not use novelty at the expense of readability.

- Treat 150 characters as a soft line-length ceiling, not a formatting target. Keep cohesive signatures, conditions, expressions, and fluent calls on one line when they fit and remain easy to scan.
- Do not mechanically wrap arguments, generic constraints, object initializers, or Boolean expressions merely to produce shorter lines. Break long code where the logical structure benefits from being visible.
- Keep one statement and one declaration per line. Wide lines are not permission to compress unrelated work.
- Name all private fields `_camelCase`, whether instance or static. Do not use `s_`, `t_`, or Hungarian-style prefixes.
- Use `PascalCase` for constants and public or internal static-readonly values.
- Within a type, normally order members as: constants and fields; properties and events; constructors and factories; public methods; protected/internal methods; private helpers; nested types.
- Keep related members and overloads together when that communicates the design better than rigid ordering. Do not reorder an existing type solely for style.
- Prefer file-scoped namespaces, collection expressions, pattern matching, primary constructors, required members, and other current C# features when they simplify the design.
- Prefer ordinary block-bodied methods when they contain meaningful behavior. Use expression bodies for genuinely simple properties, accessors, operators, and one-expression members.
- Use meaningful names and favor clarity over abbreviations. Comments should explain intent, constraints, ownership, or non-obvious decisions, not restate the code.
- Document public contracts where behavior, ownership, threading, security, or platform differences are not obvious from the signature.

## Async context

- Treat UI affinity as intentional. Code running through the Windows UI dispatcher must remain on its private `SynchronizationContext` whenever its continuation touches Win32, WebView2, window state, or other UI-thread-owned resources.
- An ordinary `await` captures that context; do not add `ConfigureAwait(false)` to a UI-affine flow. `ConfigureAwait(true)` is redundant and should normally be omitted unless spelling out the capture materially improves a delicate boundary.
- Use `ConfigureAwait(false)` in thread-agnostic lower-level code when its continuation does not touch UI-owned state. Return to UI work explicitly through `IUiDispatcher`; never assume a thread-pool continuation can access Win32 or WebView2.
- Library code must not depend on a caller-provided synchronization context unless the contract explicitly requires it. Never synchronously block to recover affinity.

## Verification

Use the narrowest relevant tests first, then the broader suite once it exists. Changes at native boundaries should include failure-path and repeated-shutdown coverage, not only happy-path tests.

For applicable changes, verify:

- Native AOT and CoreCLR builds use the same contracts.
- x64 ABI-sensitive declarations remain correct; future architectures require their own support gate.
- Framework-dependent CoreCLR, self-contained CoreCLR, and Native AOT builds use isolated intermediate/output trees and the same production contracts.
- Production publish directories contain no PDBs; symbols remain enabled and are retained as separate diagnostic artifacts.
- Partial initialization releases all previously acquired resources.
- Concurrent or repeated close requests are harmless.
- WebView subscriptions are removed, the controller closes before its parent `HWND` is destroyed, and no handles or COM references leak.
- Unauthorized, malformed, or wrong-origin frontend messages fail safely.
- Development orchestration leaves no child processes or locked files behind.
- Two instances sharing the same application/profile UDF either pass the documented multi-instance lifecycle test or trigger an explicit support-policy decision; do not silently test only separate UDFs.

## Canonical commands

Milestone 1 has introduced the canonical solution and its default project graph. Use:

```powershell
dotnet build
dotnet test
```

Both commands cover the two production assemblies, the offline interop generator, repository-local `Nanto.Testing` support, the three fast test projects, and the TestProtocol, TestApp, IntegrationTestKit, hidden WebView2 integration, and interop-generation integration projects. Only the three fast test projects execute tests by default.

Test scope is independent from build configuration. Use:

```powershell
dotnet test                              # fast tests only
dotnet test -p:TestScope=All             # fast and unattended integration tests
dotnet test -p:TestScope=Integration     # unattended integration tests only
dotnet test -c Release -p:TestScope=All  # complete suite using Release builds
```

`tests/Directory.Build.props` assigns `integration=true` to every `*IntegrationTests` assembly and applies the default fast-loop filtering. Do not add the trait to individual tests. Visible and long-running projects also receive `manual=true`; run one only by naming its project and setting both `TestScope=All` and `RunManualTests=true`. A solution-level manual opt-in is rejected. Keep `TestScope` limited to `Fast`, `All`, or `Integration`; it selects unattended test inventory and must never alter runtime, deployment, or compiler settings. The current `All` scope is the cumulative Milestone 5 gate: it retains deterministic interop and Milestone 4 asset/navigation coverage while adding hidden DPI, appearance, renderer-recovery, browser-process-exit, failure/cancellation, dependency-ordered cleanup, unlocked-storage, and zero-ledger evidence as those batches land.

Once the manual projects exist, their commands are:

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests/Nanto.Hosting.Windows.VisibleIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
dotnet test tests/Nanto.Hosting.Windows.LongRunningIntegrationTests/Nanto.Hosting.Windows.LongRunningIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

Do not run either manual project as routine verification or infer permission from a general request to build, test, continue implementation, or complete a milestone. When one is necessary to validate relevant behavior, explain why that manual test is needed and ask the user for explicit approval to run the specific project, including its effects: desktop interaction for visible tests or substantial machine time for long-running tests. After approval, name only that project; never opt manual tests into the solution-level command.
