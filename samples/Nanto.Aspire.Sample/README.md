# Nanto Aspire sample

This optional sample keeps the default Nanto development path independent from Aspire while making the resource graph inspectable when Aspire is desired.

The AppHost models three resources: a Vite frontend, the Nanto-owned native host, and a dependent HTTP service. The browser supplies an optional W3C parent context; Nanto creates the native command server activity; the command's `HttpClient` request remains beneath that activity; and the dependent service receives the same trace through standard OpenTelemetry propagation.

Run `aspire start --non-interactive` from this directory, wait for `frontend`, `dependency`, and `nanto-app`, and inspect traces and metrics through the Aspire dashboard. A trusted Aspire development certificate is required.
