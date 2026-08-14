# Narrow WinRT appearance ABI spike

This isolated spike measures the Native AOT cost of the SDK projection used to read Windows system appearance. It compares the existing
`Windows.UI.ViewManagement.UISettings` projection with a generated raw `IInspectable` ABI for only `GetColorValue` and
`ColorValuesChanged`.

The spike is not part of `Nanto.slnx`, has no production reference, and does not change Nanto's public or internal production contracts.
Its ignored output belongs under `artifacts/size-spike/winrt-appearance`.

## Design

Both compile modes use the same project, hidden Win32 window, source-generated WebView2 COM declarations, statically linked WebView2 loader,
Native AOT settings, and self-test. The only intentional difference is the appearance implementation:

- `SdkProjection` uses `Windows.UI.ViewManagement.UISettings` from `Microsoft.Windows.SDK.NET.Ref`.
- `NarrowAbi` activates the same runtime class through generated raw WinRT vtables and does not reference `WinRT.Runtime`.

The dependency-free generator reads the exact `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57 targeting pack selected by
`net10.0-windows10.0.19041.0`. The reviewed specification is `winrt-appearance-spec.json`. Generation fails if the pack version, contracts,
runtime class, default interface, selected interface, method prefix, layouts, enum values, or parameterized event-handler IID disagree.

The generated callback implements `IUnknown`, `IInspectable`, and the closed
`TypedEventHandler<UISettings, object>` ABI with atomic reference counting. The harness initializes its STA with `RoInitialize`, balances it
with `RoUninitialize`, unsubscribes before releasing the callback and `UISettings`, and exercises callback suppression, repeated disposal,
and injected cleanup failures.

.NET's source-generated COM support is limited to `IUnknown`-based interfaces, so it cannot project the `IInspectable` base used by WinRT;
the spike therefore emits raw vtables. See [ComWrappers source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation).
Direct WinRT activation also requires apartment initialization, and every successful initialization must be balanced. See
[`RoInitialize`](https://learn.microsoft.com/en-us/windows/win32/api/roapi/nf-roapi-roinitialize).

## Reproduction

Regenerate the reviewed files:

```powershell
dotnet msbuild eng/spikes/Nanto.WinRtAppearanceAbi/Generator/Nanto.WinRtAppearanceAbiGen.csproj -t:GenerateWinRtAppearanceAbi -p:Configuration=Release
```

Verify deterministic regeneration without changing tracked files:

```powershell
dotnet msbuild eng/spikes/Nanto.WinRtAppearanceAbi/Generator/Nanto.WinRtAppearanceAbiGen.csproj -t:VerifyWinRtAppearanceAbi -p:Configuration=Release
```

Publish, run, inspect, and compare both modes:

```powershell
eng/spikes/Nanto.WinRtAppearanceAbi/Measure.ps1
```

Pass `-NoRestore` only when the required packages have already been restored. The script clears only the two exact ignored mode directories,
publishes both modes in `Release` for `win-x64`, runs their hidden self-tests, creates deterministic ZIPs, inspects PE sections and Native AOT
maps, and writes `artifacts/size-spike/winrt-appearance/evidence.json`.

## Result

Measured on 2026-08-14 with .NET SDK 10.0.303, Windows 10.0.26200 x64, and targeting pack 10.0.19041.57:

| Mode | Executable | Deterministic ZIP | `WinRT.Runtime` map matches |
| --- | ---: | ---: | ---: |
| SDK projection | 3,960,832 bytes (3.777 MiB) | 1,772,811 bytes (1.691 MiB) | 10,578 |
| Narrow ABI | 2,079,232 bytes (1.983 MiB) | 976,394 bytes (0.931 MiB) | 0 |
| Difference | **1,881,600 bytes (1.794 MiB)** | **796,417 bytes (0.759 MiB)** | — |

Both modes classified the current foreground color identically and passed 100 activation/subscription/removal cycles, synthetic callback
delivery, post-disposal suppression, repeated disposal, failure cleanup, and hidden WebView2 startup and teardown. Native AOT emitted no
compiler, analyzer, trimming, marshalling, or linker warnings. The narrow executable retained no `WinRT.Runtime` matches in its map.

The experiment therefore clears its adoption threshold: the behavior matched and the narrow executable saved more than 1 MiB. This is
evidence for a later, separately reviewed production integration; this spike deliberately does not modify the existing production projection
or the architecture decision register.

The numbers are an engineering comparison, not a product size promise. The Evergreen WebView2 Runtime remains external, and PE/linker output
can change with the SDK, runtime, compiler, and operating system.
