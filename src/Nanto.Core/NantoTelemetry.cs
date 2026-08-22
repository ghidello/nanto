using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Nanto;

/// <summary>Exposes the standard diagnostics sources emitted by the Nanto runtime.</summary>
public static class NantoTelemetry
{
    public const string ActivitySourceName = "Nanto";

    public const string MeterName = "Nanto";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    public static Meter Meter { get; } = new(MeterName);

    internal static Counter<long> CommandInvocations { get; } = Meter.CreateCounter<long>("nanto.command.invocations", unit: "{invocation}");

    internal static Histogram<double> CommandDuration { get; } = Meter.CreateHistogram<double>("nanto.command.duration", unit: "ms");
}
