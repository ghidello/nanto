using System.Text.Json.Serialization;

namespace Nanto.Hosting.Windows;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WindowsApplicationIdentityDocument))]
internal sealed partial class WindowsApplicationStorageJsonContext : JsonSerializerContext;

internal sealed class WindowsApplicationIdentityDocument
{
    public required int SchemaVersion { get; init; }

    public required string ApplicationId { get; init; }
}