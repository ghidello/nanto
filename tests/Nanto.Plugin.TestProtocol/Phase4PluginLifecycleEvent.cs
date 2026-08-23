namespace Nanto.Plugin.TestProtocol;

public sealed record Phase4PluginLifecycleEvent
{
    public int SchemaVersion { get; init; } = Phase4PluginTestProtocol.SchemaVersion;

    public required string Plugin { get; init; }

    public required Phase4PluginScope Scope { get; init; }

    public required Phase4PluginLifecycleTransition Transition { get; init; }

    public required int Sequence { get; init; }
}