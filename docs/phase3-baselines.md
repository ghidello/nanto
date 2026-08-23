# Phase 3 developer-loop baselines

**Status:** Final end-to-end developer-loop baseline accepted on 2026-08-23.

Phase 3 treats developer-loop timing as engineering evidence, not a CI timeout. Measurements use the existing framework-dependent TestApp build until the generated reference template replaces it. The lifecycle marker protocol in `tests/Nanto.Cli.TestProtocol` supplies stable events for later frontend-ready, host-started, binding-updated, restart, and cleanup measurements.

## Reproduction

From the repository root on Windows x64:

```powershell
dotnet restore
./eng/Measure-Phase3Baseline.ps1
```

The script performs one clean framework-dependent build followed by 30 warm builds, then emits versioned JSON containing the cold duration, warm median, nearest-rank p95, failures, SDK/runtime environment, Node/npm versions, CPU description, and explicit placeholders for storage and power mode. Record the storage device and Windows power mode alongside retained evidence before accepting a budget.

The measured path is:

```text
TestApp C# input → Nanto generator and SDK emitter → generated C#/TypeScript → framework-dependent host output
```

It does not yet claim frontend HMR, managed Hot Reload/restart, URL readiness, or process-cleanup latency. Those measurements become valid only when their corresponding Phase 3 slices provide machine-readable start/end markers.

## Initial measurement

The first reference-machine result used .NET SDK `10.0.303`, Node `v26.3.1`, npm `11.16.0`, Windows `10.0.26200` x64, and an Intel64 Family 6 Model 140 processor. The active Windows power scheme was the user-defined `Ghidello` scheme. Storage media details were unavailable to the non-elevated measurement process and are recorded as unknown rather than inferred.

| Measure | Result |
| --- | ---: |
| Cold framework-dependent TestApp build | 11,413.4 ms |
| Warm iterations | 30 |
| Warm median | 3,151.8 ms |
| Warm nearest-rank p95 | 3,721.4 ms |
| Failures | 0 |

This is a starting observation, not yet an accepted regression budget. Slice 10 compares the completed developer loop under the same documented conditions and records any budget decision in the Phase 3 gate.

## Final end-to-end measurement

The closing run used a clean generated React/Vite consumer and the current Release CLI assembly. Readiness required both the configured Vite URL and a rendered WebView page whose generated bridge handshake had completed. Each iteration then sent a console break to the CLI's process group and required exit code `0`, no `Sample` process, no listeners on the frontend or diagnostic ports, and an exclusive open of `AppApi.cs`.

The reference machine used .NET SDK `10.0.303` / runtime `10.0.11`, Node `v26.3.1`, npm `11.16.0`, Evergreen WebView2 Runtime `151.0.4129.101`, Windows `10.0.26200` x64, and an Intel Core i7-1165G7. Storage was an Intel 670p 512 GB SSD exposed through the RAID bus. The active power scheme was the user-defined `Ghidello` scheme.

| Measure | Result |
| --- | ---: |
| Cold end-to-end readiness | 46,608.1 ms |
| Warm iterations | 30 |
| Warm median readiness | 59,578.8 ms |
| Warm nearest-rank p95 readiness | 74,984.4 ms |
| Failures | 0 |
| Shutdown median | 797.1 ms |
| Shutdown nearest-rank p95 | 1,174.1 ms |
| Shutdown maximum | 1,331.6 ms |

The accepted reference-machine budgets are 90 seconds for cold or warm end-to-end readiness, 2 seconds for graceful shutdown, and zero failed or residue-bearing iterations. The warm budget intentionally includes the template's configured `npm ci`, contract build, Vite startup, TypeScript checker, managed watch build, WebView creation, and bridge handshake.

The visible edit matrix additionally established these budgets from timestamped child output and rendered-window evidence: 2 seconds for frontend HMR/live reload, 5 seconds for supported managed Hot Reload, and 30 seconds for a contract-changing managed restart, regenerated client, frontend type check, and bridge re-handshake. The observed managed method-body edit applied in 2,242 ms; the incompatible contract edit surfaced its TypeScript error and recreated the host within the 30-second budget.
