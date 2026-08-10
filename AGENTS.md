# AGENTS.md

## Project

Nanto is an early-stage .NET framework for small native applications with web frontends. The committed MVP target is Windows, using raw Win32, the Evergreen WebView2 Runtime, and Native AOT by default.

The current source of truth is [`docs/Nanto-architecture-and-roadmap.md`](docs/Nanto-architecture-and-roadmap.md). Read the relevant sections before making architectural changes. If implementation and documentation disagree, call out the mismatch rather than silently changing direction.

## Current priority

Work from the roadmap in order. Phase 0 was completed in the historical [Telaio repository](https://github.com/ghidello/telaio/tree/phase_1); the immediate focus is Phase 1 as specified in `docs/phase1-plan.md`: establish portable lifecycle contracts and tests, then bring the proven raw-Win32/WebView2 x64 approach into production through narrow vertical milestones. Nanto must not reference or copy Telaio's Phase 0 assemblies. Avoid templates, plugins, DI infrastructure, multi-window support, packaging, polished UI, and future-platform work during this phase.

## Engineering constraints

- Keep portable public APIs free of Win32, WebView2, WinUI, MAUI, WPF, WinForms, and other platform types. Put explicit platform escape hatches behind optional interfaces.
- On Windows, Nanto owns the `HWND`; WebView2 attaches to it. Do not introduce WinUI 3 or Windows App SDK dependencies.
- Prefer generated, pinned interop definitions (not hand-copied signatures or raw OS magic numbers), and use `DisableRuntimeMarshalling=true` for the AOT path.
- Generate commands, serializers, plugin registries, capabilities, and TypeScript bindings at compile time. Avoid runtime assembly scanning, dynamic proxies, runtime code emission, and unbounded reflection.
- Treat frontend content as untrusted. Command access is default-deny and must be checked against the calling origin and window/WebView identity.
- Give every native handle, COM object, subscription, cancellation source, and process one clear owner. Teardown must be deterministic, idempotent, cancellation-first, and reverse creation order.
- Keep WebView2 work on the STA UI thread. Never synchronously block on an operation whose completion requires that thread.
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

Treat `.editorconfig` as the source of truth for formatting and naming. Use modern C# features supported by the pinned SDK when they make code clearer, but do not use novelty at the expense of readability.

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

## Verification

Use the narrowest relevant tests first, then the broader suite once it exists. Changes at native boundaries should include failure-path and repeated-shutdown coverage, not only happy-path tests.

For applicable changes, verify:

- Native AOT and CoreCLR builds use the same contracts.
- x64 ABI-sensitive declarations remain correct; future architectures require their own support gate.
- Partial initialization releases all previously acquired resources.
- Concurrent or repeated close requests are harmless.
- WebView subscriptions are removed, the controller closes before its parent `HWND` is destroyed, and no handles or COM references leak.
- Unauthorized, malformed, or wrong-origin frontend messages fail safely.
- Development orchestration leaves no child processes or locked files behind.

When the repository gains canonical build, test, format, and publish commands, record them here rather than guessing or inventing alternatives.

## Canonical commands

This documentation-and-configuration baseline intentionally has no solution or projects, so it has no build or test commands yet. Record canonical commands here when the corresponding Phase 1 projects are introduced; do not infer commands from the historical Telaio repository.
