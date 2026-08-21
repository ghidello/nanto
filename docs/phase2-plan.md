# Phase 2 plan — versioned IPC and generated contracts

**Status:** In progress.

Phase 2 adds the versioned, generated frontend bridge without weakening the completed Phase 1 Windows host, lifecycle, asset, appearance, diagnostics, or deployment guarantees. The complete 550-process lifecycle/recovery soak and a visible mixed-DPI run on two suitable monitors remain open Phase 1 acceptance follow-ups; neither is waived or considered passed.

## Decisions

- Individual API classes require only `[NantoCommand]` methods or `[NantoEvent]` properties. The containing type supplies the frontend group name, with an `Api` suffix removed.
- `[NantoApi]` marks a composed group root. `[NantoApiPart<TApi>]` lets separately constructed classes contribute to that group without a string name.
- CLR namespaces never form part of the frontend API. Partial declarations are aggregated, and generated-name collisions fail compilation.
- Application services are registered explicitly through generated `NantoBridgeConfiguration.Add(...)` overloads. Nanto neither constructs nor disposes them.
- Window access is separately default-deny and uses generated `AppCapabilities` values. Registration never grants access implicitly and Phase 2 provides no group wildcard.
- Expected application failures use `NantoResult<T, TError>`. Transport, authorization, lifecycle, protocol, and unexpected handler failures reject with a sanitized frontend `NantoCommandError`. Caller cancellation follows `AbortSignal`/`AbortError` semantics.
- `[NantoEvent] NantoEvent<T>` is hot and non-replayed. The generated frontend exposes an explicitly cancellable async-iterable subscription.
- JSON is the Phase 2 transport. Command and event IDs are private generated values; public code, manifests, capabilities, and diagnostics retain symbolic names.
- Binary command and event types are rejected while Phase 2 is JSON-only. A `Uint8Array` projection requires a future explicit binary mapping instead of exposing JSON base64 strings under a misleading type.
- TypeScript is the canonical frontend implementation. A pinned TypeScript compiler produces ESM JavaScript, declarations, and source maps; there is no independent JavaScript emitter.

## Delivery sequence

1. Add the portable bridge, result, capability, invocation-context, and event contracts.
2. Specify and test the versioned session/request/response protocol, authorization, cancellation, and sanitized failure behavior.
3. Add the incremental C# generator and the shared semantic model used by the SDK TypeScript emitter.
4. Exercise one unary generated command through the existing origin-checked WebView2 boundary under CoreCLR and Native AOT.
5. Add pull-based command streams and bounded typed event subscriptions with deterministic shutdown.
6. Add the pinned npm workspace, generated application fixture, TypeScript type/runtime tests, and compiled JavaScript verification.
7. Run the complete unattended Release gate without opting into either manual Phase 1 follow-up.

## Implementation progress

As of 21 August 2026, the portable contracts, protocol v1 session, generated registration and dispatch, source-generated JSON metadata,
TypeScript emitter, XML-documentation and normalized source-location preservation, compiled ESM package, bounded streams and events, and the Windows WebView2 adapter are implemented. The real hidden WebView2
suite exercises typed unary results, application failures, pull streams, hot events, caller cancellation, navigation session rotation, and close-time
cancellation. It also rejects malformed requests, manifest mismatches, unauthorized commands, stale sessions, and wrong-origin iframe traffic through the
real browser boundary. Generator coverage now pins representative C# and TypeScript golden fingerprints, syntax-tree ordering, unchanged-input incremental
caching, and additional rejected command and event shapes. The latest unattended Release gate passed 438 tests across the fast, hidden WebView2,
self-contained CoreCLR, and Native AOT projects;
the manual visible and long-running projects did not execute their opted-in scenarios.

Phase 2 remains in progress. The next closeout slice is an exhaustive audit of the remaining rejected-signature generator diagnostics and the Phase 2 exit record.
The complete 550-process soak and mixed-DPI visible run remain separately approved Phase 1 follow-ups.

## Acceptance

- Contract changes deterministically change generated C#, the symbolic manifest, and TypeScript.
- Invalid or ambiguous contracts fail at compile time with source diagnostics.
- CoreCLR and Native AOT use the same generated registry, JSON metadata, authorization, and lifecycle paths.
- No runtime assembly scanning, dynamic proxies, runtime code emission, or unbounded reflection is used.
- Wrong-origin, malformed, stale-session, unauthorized, oversized, and unknown-command messages fail safely.
- Cancellation, navigation, close, late completion, stream disposal, event overflow, and repeated teardown are race-tested.
- Strict Native AOT publish produces no unexplained trimming or AOT warnings.
