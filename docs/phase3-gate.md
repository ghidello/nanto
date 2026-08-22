# Phase 3 gate record

**Status:** Open. The implementation candidate is complete enough for automated validation, but Phase 3 is not closed until the live developer-loop and orchestration evidence below is recorded.

This record separates implemented behavior from evidence that requires a live native development session, another package manager, or machine-specific Aspire prerequisites. It does not waive the Phase 1 550-process soak or mixed-DPI two-monitor follow-ups.

## Proven implementation

- `Nanto.Cli` provides strict configuration and overlays, deterministic redacted human/JSON plans, `dev`, `build`, and actionable `doctor` checks.
- Development content uses an explicit normalized origin; the Windows host authorizes bridge messages only for that exact origin and WebView identity.
- Owned frontend and managed processes use bounded output, readiness checks, cancellation-first shutdown, and a kill-on-close Windows job. Integration coverage proves parent/grandchild termination, file-handle release, and bounded flood output.
- The SDK atomically emits generated TypeScript, avoids timestamp changes for identical output, builds deterministic embedded-asset manifests, and removes stale temporary files.
- The React/Vite template was installed from locally packed NuGet packages in a clean external-consumer directory. Its default `nanto build` completed with npm reporting zero vulnerabilities and produced one 4,965,888-byte Native AOT executable with no PDB, dynamic WebView2 loader, managed DLL, or CoreCLR runtime.
- Native AOT artifact inspection now rejects a CoreCLR-shaped directory, preventing runtime labels from masking a restore/property handoff error.
- `NantoTelemetry` exposes optional standard .NET activities and bounded metrics. The generated browser runtime accepts dependency-free W3C provider/observer hooks; malformed and over-limit context is ignored.
- `Nanto.Hosting.Aspire` remains optional and exposes a minimal resource graph. The sample composes a vanilla TypeScript/Vite frontend, Nanto app, and dependent ASP.NET service with standard OpenTelemetry configuration.

## Automated evidence

The implementation pass recorded these successful checks before the final Release gate:

| Check | Result |
| --- | --- |
| CLI fast tests | 38 passed |
| Core fast tests | 113 passed |
| Frontend runtime tests | 17 passed |
| Aspire resource-model tests | 2 passed |
| Dev-loop process integration | 2 passed |
| Canonical Debug build | 33 projects, 0 warnings, 0 errors |
| Canonical default test scope | 432 passed, 0 failed |
| Instrumented Aspire sample Native AOT publish | One native executable, no warnings |
| Unattended Release gate (`TestScope=All`) | 518 passed, 0 failed; manual tests remained filtered |

The Release gate includes the hidden WebView, self-contained CoreCLR, and Native AOT deployment smoke lanes. Discovering the manual assemblies does not execute their tests without `RunManualTests=true`.

## Open Phase 3 evidence

- Run a real `dotnet nanto dev` session with the native window to measure frontend HMR, supported managed Hot Reload, incompatible-edit restart/re-handshake, contract-edit type checking, and Ctrl+C shutdown. The run is visible and therefore requires explicit approval in an automated session.
- Record one cold and 30 warm end-to-end iterations with median, p95, failure count, storage, and power-mode evidence. The initial direct-build baseline remains in [`phase3-baselines.md`](phase3-baselines.md); it is not an accepted final budget.
- Exercise the custom-SPA template end to end and repeat the package-manager matrix with pnpm, including paths containing Unicode. npm and paths containing spaces are proven.
- Run the Aspire AppHost and verify dashboard resource health plus correlated traces/metrics and clean shutdown. The installed Aspire CLI is `13.5.0`; local diagnostics currently report a missing DCP certificate, an untrusted development certificate, and Docker not running. Static AppHost and resource-model builds pass.
- Strengthen process creation so an arbitrary configured executable cannot spawn a descendant in the small interval between `Process.Start` and assignment to the kill-on-close job. Current contained fixtures delay descendant creation and prove cleanup after assignment.

## Related open Phase 1 follow-ups

- Complete the separately approved 550-process lifecycle/recovery soak.
- Pass the visible cross-monitor scenario on two active monitors with different effective DPI.

Neither item is part of the unattended Phase 3 gate, and neither is reported as passed.
