using System.Text.Json.Serialization;

namespace Nanto.Cli.TestProtocol;

[JsonConverter(typeof(JsonStringEnumConverter<Phase3LifecycleMarker>))]
public enum Phase3LifecycleMarker
{
    FrontendReady,
    HostStarted,
    GeneratedClientUpdated,
    HostRestarted,
    CleanupComplete,
}
