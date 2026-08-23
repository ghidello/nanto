using System.Text.Json.Serialization;

namespace Nanto.Plugin.TestProtocol;

[JsonConverter(typeof(JsonStringEnumConverter<Phase4PluginScope>))]
public enum Phase4PluginScope
{
    Application,
    Window,
}