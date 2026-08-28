using System.Text.Json;

namespace Nanto.Sdk.Plugins;

internal sealed record NantoPluginManifestReference(string ManifestPath, string PackageId);

internal static class NantoPluginSelectionReader
{
    private const string PluginManifestPackagePath = "nanto/plugin-manifest-v1.json";

    public static NantoPluginManifestInput[] Read(string assetsFile, string targetFramework, IEnumerable<NantoPluginManifestReference> references)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);
        ArgumentNullException.ThrowIfNull(references);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(assetsFile), new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            JsonElement libraries = root.GetProperty("libraries");
            JsonElement targets = root.GetProperty("targets");
            if (!targets.TryGetProperty(targetFramework, out JsonElement target))
            {
                throw new InvalidDataException($"The restore graph does not contain target framework '{targetFramework}'.");
            }

            string[] packageFolders = root.GetProperty("packageFolders").EnumerateObject()
                .Select(static property => Path.TrimEndingDirectorySeparator(Path.GetFullPath(property.Name)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedVersions = target.EnumerateObject()
                .Where(static library => library.Value.TryGetProperty("type", out JsonElement type) && type.GetString() == "package")
                .Select(static library => SplitLibraryIdentity(library.Name))
                .ToDictionary(static identity => identity.PackageId, static identity => identity.Version, StringComparer.OrdinalIgnoreCase);
            return references.Select(reference => Resolve(reference, libraries, target, packageFolders, selectedVersions)).ToArray();
        }
        catch (InvalidDataException exception)
        {
            throw new NantoPluginManifestException("NANTO4114", "project.assets.json", "$", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
            throw new NantoPluginManifestException("NANTO4114", "project.assets.json", "$", "The evaluated restore graph could not be read.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            _ = exception;
            throw new NantoPluginManifestException("NANTO4114", "project.assets.json", "$", "The evaluated restore graph is malformed.");
        }
    }

    private static NantoPluginManifestInput Resolve(
        NantoPluginManifestReference reference,
        JsonElement libraries,
        JsonElement target,
        string[] packageFolders,
        Dictionary<string, string> selectedVersions)
    {
        string manifestPath = Path.GetFullPath(reference.ManifestPath);
        foreach (JsonProperty library in libraries.EnumerateObject())
        {
            int separator = library.Name.LastIndexOf('/');
            if (separator <= 0
                || !library.Name[..separator].Equals(reference.PackageId, StringComparison.OrdinalIgnoreCase)
                || !library.Value.TryGetProperty("type", out JsonElement type)
                || type.GetString() != "package"
                || !library.Value.TryGetProperty("path", out JsonElement relativeRootProperty)
                || relativeRootProperty.GetString() is not { } relativeRoot)
            {
                continue;
            }

            foreach (string packageFolder in packageFolders)
            {
                string packageRoot = Path.GetFullPath(relativeRoot.Replace('/', Path.DirectorySeparatorChar), packageFolder);
                if (!manifestPath.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativeManifest = Path.GetRelativePath(packageRoot, manifestPath).Replace('\\', '/');
                if (!string.Equals(relativeManifest, PluginManifestPackagePath, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Plugin manifest for package '{reference.PackageId}' must use package path '{PluginManifestPackagePath}'.");
                }

                bool declaredFile = library.Value.TryGetProperty("files", out JsonElement files)
                    && files.EnumerateArray().Any(file => string.Equals(file.GetString(), relativeManifest, StringComparison.Ordinal));
                if (!declaredFile)
                {
                    throw new InvalidDataException($"Plugin manifest for package '{reference.PackageId}' is not a declared package file.");
                }

                if (!target.TryGetProperty(library.Name, out JsonElement targetLibrary))
                {
                    throw new InvalidDataException($"Plugin package '{reference.PackageId}' is absent from the selected target framework.");
                }

                var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (targetLibrary.TryGetProperty("dependencies", out JsonElement dependencyObject))
                {
                    foreach (JsonProperty dependency in dependencyObject.EnumerateObject())
                    {
                        if (!selectedVersions.TryGetValue(dependency.Name, out string? selectedVersion))
                        {
                            throw new InvalidDataException($"Dependency '{dependency.Name}' of plugin package '{reference.PackageId}' is absent from the selected restore graph.");
                        }

                        dependencies.Add(dependency.Name, selectedVersion);
                    }
                }

                var packageFiles = library.Value.GetProperty("files").EnumerateArray()
                    .Select(static file => file.GetString()!)
                    .ToHashSet(StringComparer.Ordinal);

                return new NantoPluginManifestInput(
                    manifestPath,
                    reference.PackageId + "/" + relativeManifest,
                    packageRoot,
                    library.Name[..separator],
                    library.Name[(separator + 1)..],
                    dependencies,
                    packageFiles);
            }
        }

        throw new InvalidDataException($"Plugin manifest for package '{reference.PackageId}' does not belong to the evaluated restore graph.");
    }

    private static (string PackageId, string Version) SplitLibraryIdentity(string identity)
    {
        int separator = identity.LastIndexOf('/');
        if (separator <= 0 || separator == identity.Length - 1)
        {
            throw new InvalidDataException($"Restore library identity '{identity}' is malformed.");
        }

        return (identity[..separator], identity[(separator + 1)..]);
    }
}
