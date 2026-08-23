# Phase 1 Gate Record

## Status

Phase 1 implementation is closed as of August 14, 2026. Phase 2 may proceed.

One acceptance follow-up remains open:

1. Pass `VisibleCrossMonitorDpi` on two active monitors with different effective DPI.

The complete 550-process lifecycle and renderer-recovery soak passed on August 23, 2026. The remaining mixed-DPI item is neither waived nor reported as passed. It qualifies the strength of Phase 1 acceptance evidence, not the completeness of the implemented production surface.

## Implementation and environment

- Closing implementation commit: `d22d3543d80a7d3de7ca58f695c7207aa61b2f9b`
- SDK: .NET SDK `10.0.303`
- Runtime: `.NET 10.0.11`
- OS observed by TestApp: `Microsoft Windows 10.0.26200`, x64
- Evergreen WebView2 Runtime: `151.0.4129.78`

Detailed machine-specific reports, packages, symbols, maps, logs, and screenshots remain ignored beneath `artifacts/phase1/**`.

## Deployment evidence

The unattended deployment projects produced successful structural inspection and `HostLifecycle`, `Navigation`, and `RendererRecovery` smoke evidence for both production modes:

| Mode | Deployed bytes | Deterministic ZIP bytes | ZIP SHA-256 | Loader |
| --- | ---: | ---: | --- | --- |
| Self-contained CoreCLR | 107,263,460 | 42,391,958 | `672b531e74366baf657f8e1fba8dfc128c354d64e86f792b61f05aef1e29fb75` | Trusted adjacent Microsoft DLL |
| Native AOT | 5,877,248 | 2,427,724 | `a2acb8751f142b679d2ad7c257aea8721c00f74b934b88ad4836760884d636ff` | Statically linked; no loader DLL |

The publish directories contain no PDBs; symbols are retained separately. The Evergreen WebView2 Runtime is installed externally and is not included in these sizes.

## Visible acceptance

The explicitly approved command was:

```powershell
dotnet test tests/Nanto.Hosting.Windows.VisibleIntegrationTests/Nanto.Hosting.Windows.VisibleIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

Result at `d22d354`:

- `VisibleDesktop`: passed. Foreground/focus, F6 delivery, client-DIP resize, System/Dark/Light behavior, native-frame synchronization, SPA `prefers-color-scheme`, screenshots, browser exit, unlocked storage, dependency-ordered cleanup, and a zero final ledger succeeded.
- `VisibleCrossMonitorDpi`: skipped with `InsufficientDisplays`. The available topology contained one 1920×1080 monitor at 96 DPI.
- Retained evidence: `artifacts/phase1/visible/1dcc36f164bb4054bd768dd8d8298a98/` and `artifacts/phase1/visible/38ff7763e9664419ad53a0b4cfb2e2a2/`.

The mixed-DPI result remains pending until the same command passes both scenarios on suitable hardware.

## Long-running acceptance

The explicitly approved command was:

```powershell
dotnet test tests/Nanto.Hosting.Windows.LongRunningIntegrationTests/Nanto.Hosting.Windows.LongRunningIntegrationTests.csproj -p:TestScope=All -p:RunManualTests=true
```

The August 14 attempt used the former 45-minute deadline and completed 397 clean isolated processes: 362 `HostLifecycle` and 35 `RendererRecovery`, including seven complete blocks. It then stopped at the outer deadline, terminated containment, and successfully deleted the shared application root. No behavioral or resource failure was observed in a completed child. Retained summary: `artifacts/phase1/long-running/b220e05456914f60a2df7be2b979734b/summary.json`.

The approved August 23 acceptance run `e342d4fdce69441595c25fced19c9fd6` completed all ten blocks and passed 500 `HostLifecycle` plus 50 `RendererRecovery` processes in 3,843,760 ms, within the 75-minute deadline. It recorded no first failure, deleted the shared application root, and completed every block with the expected 50/5 scenario split. Retained summary: `artifacts/phase1/long-running/e342d4fdce69441595c25fced19c9fd6/summary.json`.

An earlier August 23 attempt reached 479 successful processes before one WebView activation exceeded the fixed timeout while the machine was under user-reported memory pressure. That attempt released every acquired resource and deleted its application root. The clean controlled rerun passed the exact prior failure point and all remaining processes; the failed attempt remains retained as interference evidence rather than acceptance evidence.

## Unsupported and deferred scope

- Windows x64 and the Evergreen WebView2 Runtime are the only Phase 1 deployment target.
- Arm64, other operating systems, packaging/installers, runtime bundling, multi-window behavior, plugins, and SDK/frontend tooling remain later roadmap phases.
- Phase 1 size figures are TestApp engineering baselines, not marketed minimal-application sizes or numeric regression budgets.
