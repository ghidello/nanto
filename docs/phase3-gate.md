# Phase 3 gate record

**Status:** Closed on 2026-08-23. Implementation, automated validation, live developer-loop evidence, Aspire orchestration, and final repeated measurements pass.

This record closes Phase 3 without waiving the Phase 1 550-process soak or mixed-DPI two-monitor follow-ups.

## Proven implementation

- `Nanto.Cli` provides strict configuration and overlays, deterministic redacted human/JSON plans, `dev`, `build`, and actionable `doctor` checks.
- Development content uses an explicit normalized origin; the Windows host authorizes bridge messages only for that exact origin and WebView identity.
- Owned frontend and managed processes use bounded output, readiness checks, cancellation-first shutdown, and a kill-on-close Windows job. Windows process creation supplies that job through `PROC_THREAD_ATTRIBUTE_JOB_LIST`, so descendants are contained atomically from process creation rather than after `Process.Start`. Integration coverage proves immediate parent/grandchild termination, file-handle release, rapid-exit observation, and bounded flood output.
- The SDK atomically emits generated TypeScript, avoids timestamp changes for identical output, builds deterministic embedded-asset manifests, and removes stale temporary files.
- The React/Vite template was installed from locally packed NuGet packages in a clean external-consumer directory. Its default `nanto build` completed with npm reporting zero vulnerabilities and produced one 4,965,888-byte Native AOT executable with no PDB, dynamic WebView2 loader, managed DLL, or CoreCLR runtime.
- The custom template was installed from locally packed packages, attached to a plain TypeScript/Vite SPA, and configured with pnpm 11.19.0 in a path containing a space and `é`. The packaged CLI completed framework-dependent CoreCLR and Native AOT production builds against the generated ESM client; installed payloads were 26,546,185 and 4,772,864 bytes respectively.
- Native AOT artifact inspection now rejects a CoreCLR-shaped directory, preventing runtime labels from masking a restore/property handoff error.
- `NantoTelemetry` exposes optional standard .NET activities and bounded metrics. The generated browser runtime accepts dependency-free W3C provider/observer hooks; malformed and over-limit context is ignored.
- `Nanto.Hosting.Aspire` remains optional and exposes a minimal resource graph. The sample composes a vanilla TypeScript/Vite frontend, Nanto app, and dependent ASP.NET service with standard OpenTelemetry configuration.

## Automated evidence

The closing implementation pass recorded these successful checks:

| Check | Result |
| --- | --- |
| CLI fast tests | 44 passed |
| Core fast tests | 113 passed |
| Frontend runtime tests | 17 passed |
| Aspire resource-model tests | 2 passed |
| Dev-loop process integration | 3 passed |
| Canonical Debug build | 33 projects, 0 warnings, 0 errors |
| Canonical Release default test scope | 433 passed, 0 failed |
| Instrumented Aspire sample Native AOT publish | One native executable, no warnings |
| Formatting verification | `dotnet format --verify-no-changes --no-restore` passed |
| Unattended Release gate (`TestScope=All`) | 525 passed, 0 failed; manual tests remained filtered |

The Release gate includes the hidden WebView, self-contained CoreCLR, and Native AOT deployment smoke lanes. Discovering the manual assemblies does not execute their tests without `RunManualTests=true`.

## Live developer-loop evidence

- A real native `dotnet nanto dev` session visibly applied a React/Vite frontend update without changing the host PID. Moving bridge bootstrap into a stable module prevents HMR from repeating the top-level handshake; Vite may use a full-page live reload when React Fast Refresh invalidates the entry module, still without restarting the native host.
- A supported C# method-body edit applied in 2,242 ms in the same host process, and the rendered window returned the new native command result.
- An incompatible command-signature edit reported that restart was required, replaced the host process, atomically regenerated the client, and caused the Vite TypeScript checker to report the expected arity error. Correcting the frontend call returned the checker to zero errors, and the recreated window invoked the new command through a fresh bridge session.
- Ctrl+C initially exposed forced 20-second cleanup. The closing implementation creates child console process groups when a console is attached, sends a group-scoped Ctrl+Break before closing stdin, and retains kill-on-close Job containment as the fallback. The verified path exits `0`, releases the source file, and leaves no host process or frontend listener.
- `doctor` now uses the documented Evergreen WebView2 product registration and reports installed runtime `151.0.4129.101` instead of the previous false negative.

## Aspire runtime evidence

- The AppHost reported the dependency, frontend, and Nanto resources healthy. The native frontend command produced one distributed trace containing the Nanto server activity, its HTTP client activity, and the dependency's `/ping` server activity, all with successful outcomes.
- The dashboard exposed `nanto.command.duration` and `nanto.command.invocations`; the invocation series carried bounded command ID, `unary` kind, and `ok` outcome tags.
- `aspire stop --non-interactive` shut down the AppHost cleanly. The sample uses only project and executable resources, so the stopped Docker service was not required.

## Final measurement and budgets

The clean reference run recorded one cold and 30 warm bridge-ready launches with zero failures. Cold readiness was 46,608.1 ms; warm median was 59,578.8 ms and nearest-rank p95 was 74,984.4 ms. Shutdown median was 797.1 ms, p95 was 1,174.1 ms, and maximum was 1,331.6 ms. The accepted budgets are 90 seconds for end-to-end readiness, 2 seconds for shutdown, 2 seconds for frontend feedback, 5 seconds for managed Hot Reload, 30 seconds for incompatible-edit restart/re-handshake, and zero failures. Full environment and procedure details are in [`phase3-baselines.md`](phase3-baselines.md).

## Related open Phase 1 follow-ups

- Complete the separately approved 550-process lifecycle/recovery soak.
- Pass the visible cross-monitor scenario on two active monitors with different effective DPI.

Neither item is part of the unattended Phase 3 gate, and neither is reported as passed.
