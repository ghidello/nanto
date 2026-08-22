# Phase 3 developer-loop baselines

**Status:** Initial direct build/generation baseline recorded on 2026-08-22. Frontend and orchestration measurements remain pending their implementation slices.

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
