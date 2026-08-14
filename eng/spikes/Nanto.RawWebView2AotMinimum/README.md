# Raw WebView2 Native AOT minimum

This tracked spike answers a deliberately exaggerated question: how small can an honest Windows x64 Native AOT executable be while it still
creates a hidden Win32 window, starts the Evergreen WebView2 Runtime through the statically linked loader, creates a controller, navigates to
HTML, observes navigation completion, and releases its resources?

The current answer is **1,629,696 bytes (1.554 MiB)**, or **776,248 bytes (0.740 MiB)** in the deterministic ZIP produced by the measurement
script.

## What it is

- One self-contained, fully trimmed Native AOT executable.
- Raw Win32 ownership with a hidden top-level window.
- A statically linked `WebView2LoaderStatic.lib`; neither the DLL nor the static library is deployed.
- A narrow WebView2 ABI generated from the pinned package IDL and header by Nanto's offline generator.
- CsWin32-generated operating-system declarations.
- A constant in-memory HTML document, avoiding an asset subsystem that would distort the lower-bound measurement.
- A process exit code as the only test protocol.

The committed projection contains the complete same-interface vtable prefixes required to reach the selected methods. Its manifest records the
pinned WebView2 package version, input hashes, interface closure, generated-source hash, encoding, and line endings.

## What it deliberately is not

This is not a Nanto customer application, template, supported host, or architectural shortcut. It excludes Nanto's lifecycle coordination,
storage identity, versioned assets, secure virtual-host navigation, appearance, renderer recovery, logging, cancellation, fault aggregation,
DPI behavior, and test protocol. The Evergreen WebView2 Runtime is external and is not included in the measured bytes.

Disabling stack traces, EventSource, metadata updates, debugger support, and localized runtime error strings makes this a lower-bound experiment,
not an appropriate default configuration. The word “minimum” means “smallest useful and reproducible baseline we measured,” not a proof that no
smaller valid PE can exist. Hyperbole has limits, apparently.

## Reproduction

The spike is intentionally outside `Nanto.slnx`. Run it directly:

```powershell
eng/spikes/Nanto.RawWebView2AotMinimum/Measure.ps1
```

Pass `-NoRestore` only when both the offline generator and spike project have already been restored. The script:

1. regenerates the ABI into `obj` and compares it byte-for-byte with the committed files;
2. publishes `Release` Native AOT for `win-x64` with map generation;
3. requires exactly one deployable executable;
4. runs the hidden WebView2 navigation smoke test;
5. proves the WebView2 user-data directory can be deleted after exit;
6. checks the AOT map contains no Nanto production assembly symbols;
7. writes ignored PE, size, hash, package, and behavior evidence beneath
   `artifacts/size-spike/raw-webview2-aot-minimum`.

## Recorded result

Measured on 2026-08-14 with .NET SDK 10.0.303, Windows 10.0.26200 x64, and Microsoft.Web.WebView2 1.0.4129.50:

| Artifact | Size |
| --- | ---: |
| Native executable | 1,629,696 bytes (1.554 MiB) |
| Deterministic ZIP | 776,248 bytes (0.740 MiB) |

The hidden window, WebView2 creation, in-memory navigation, navigation-completed callback, reverse-order release, process exit, and user-data
unlock checks passed. Native AOT emitted no compiler, analyzer, trimming, marshalling, or linker warnings, and the deployment contained one file.

These figures are engineering evidence for understanding fixed runtime costs. They are not a marketed application-size promise and should not
be compared with the Nanto TestApp without accounting for the production behavior intentionally excluded here.
