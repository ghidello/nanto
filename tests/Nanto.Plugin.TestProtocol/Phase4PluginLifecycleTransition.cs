using System.Text.Json.Serialization;

namespace Nanto.Plugin.TestProtocol;

[JsonConverter(typeof(JsonStringEnumConverter<Phase4PluginLifecycleTransition>))]
public enum Phase4PluginLifecycleTransition
{
    Created,
    ConfigureEntered,
    Configured,
    StartEntered,
    Started,
    StopEntered,
    Stopped,
    DisposeEntered,
    Disposed,
    LeaseRevoked,
    Abandoned,
}