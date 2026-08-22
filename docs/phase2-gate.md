# Phase 2 Gate Record

## Status

Phase 2 implementation is closed as of August 22, 2026. Phase 3 may proceed.

The complete 550-process lifecycle/recovery soak and the visible mixed-DPI run on two suitable monitors remain open Phase 1 acceptance follow-ups. They are neither waived nor reported as passed; their exact status remains in [`phase1-gate.md`](phase1-gate.md).

## Implemented surface

- Protocol v1 handshake and manifest binding, default-deny per-window capabilities, origin and WebView identity checks, cancellation, bounded errors, streams, and hot events.
- Reflection-free generated registration, dispatch, JSON metadata, symbolic manifests, capabilities, and ESM TypeScript bindings.
- Deterministic private 32-bit command IDs with compile-time collision diagnostics and full SHA-256 session manifest fingerprints.
- Framework-neutral frontend transport and a pinned TypeScript build producing JavaScript, declarations, and source maps.
- The same generated contracts and lifecycle paths under framework-dependent CoreCLR, self-contained CoreCLR, and Native AOT.

## Exit evidence

The August 22 unattended Release gate completed successfully:

```powershell
dotnet test -c Release -p:TestScope=All --no-restore
```

Result: 454 tests passed, zero failed, and zero skipped. The manual visible and long-running assemblies built, but their opted-in scenarios did not execute.

The frontend workspace also passed all 15 compiled runtime tests:

```powershell
cd frontend
npm test
```

Focused generator coverage passed 55 cases. It includes every reachable rejection category, invalid composed API roots, a reproducible 32-bit identifier collision, deterministic C# and TypeScript golden fingerprints, syntax-tree ordering, and unchanged-input incremental caching.

The real hidden WebView2 suite passed 69 cases. Its bridge coverage includes unary results, expected failures, streams, events, caller cancellation, navigation session rotation, close-time cancellation, and rejection of malformed, manifest-mismatched, unauthorized, stale-session, and wrong-origin traffic.

## Exit criteria

- Contract and DTO changes deterministically affect the canonical signature, manifest, generated C#, and TypeScript fixtures.
- Unsupported and ambiguous contracts fail at compile time with source diagnostics.
- Runtime command and DTO discovery use generated registries and serializers; no assembly scanning, dynamic proxy, or runtime code generation path was introduced.
- Self-contained CoreCLR and Native AOT deployment tests exercise the production generated contracts; strict Native AOT publish reports no unexplained trimming or AOT warnings.
- Protocol, frontend, and real-WebView tests cover authorization, malformed and oversized messages, unknown commands, cancellation races, navigation, close, late completion, stream disposal, bounded event overflow, and repeated teardown.

All Phase 2 implementation and exit criteria are satisfied.

## Deferred scope

- Binary bridge payloads remain rejected while the protocol is JSON-only.
- CLI, templates, development orchestration, HMR, and telemetry belong to Phase 3.
- Plugins, broader capability policy, multi-window behavior, packaging, and additional platform hosts remain in their later roadmap phases.
