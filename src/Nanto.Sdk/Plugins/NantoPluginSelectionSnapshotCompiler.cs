using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nanto.Sdk.Plugins;

internal static class NantoPluginSelectionSnapshotCompiler
{
    private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static NantoPluginSelectionSnapshotDocument Create(
        string assetsFile,
        string projectFile,
        string targetFramework,
        string configuration,
        NantoPluginCatalogDocument catalog,
        IEnumerable<KeyValuePair<string, string>> restoreProperties,
        IEnumerable<string> restoreInputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(restoreProperties);
        ArgumentNullException.ThrowIfNull(restoreInputs);

        string fullProject = Path.GetFullPath(projectFile);
        string projectDirectory = Path.GetDirectoryName(fullProject)!;
        string assetsHash = Hash(ReadStableFile(assetsFile, "project.assets.json"));
        NantoPluginSelectionProperty[] properties = restoreProperties
            .GroupBy(static property => property.Key, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .Select(property => new NantoPluginSelectionProperty
            {
                Name = property.Key,
                ValueSha256 = Hash(Encoding.UTF8.GetBytes(NormalizeRestoreProperty(property.Key, property.Value, projectDirectory))),
            })
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();
        NantoPluginSelectionInput[] inputs = CollectInputs(fullProject, projectDirectory, restoreInputs.Concat(ReadAssetsRestoreInputs(assetsFile)));
        string hostProject = Path.GetFileName(fullProject);
        string fingerprint = Hash(Canonicalize(
            hostProject,
            targetFramework,
            configuration,
            assetsHash,
            catalog.Fingerprint,
            properties,
            inputs));
        return new NantoPluginSelectionSnapshotDocument
        {
            Fingerprint = fingerprint,
            HostProject = hostProject,
            TargetFramework = targetFramework,
            Configuration = configuration,
            AssetsFileSha256 = assetsHash,
            CatalogFingerprint = catalog.Fingerprint,
            RestoreProperties = properties,
            RestoreInputs = inputs,
            Plugins = catalog.Plugins,
        };
    }

    public static void EnsureFresh(string snapshotPath, NantoPluginSelectionSnapshotDocument candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!File.Exists(snapshotPath))
        {
            throw Error("The plugin selection snapshot is missing; restore and build are required.");
        }

        NantoPluginSelectionSnapshotDocument existing;
        try
        {
            using FileStream stream = File.OpenRead(snapshotPath);
            existing = JsonSerializer.Deserialize(stream, NantoPluginSelectionSnapshotJsonContext.Default.NantoPluginSelectionSnapshotDocument)
                ?? throw new InvalidDataException("Selection snapshot is empty.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _ = exception;
            throw Error("The existing plugin selection snapshot is unreadable; restore is required.");
        }

        if (existing.Fingerprint == candidate.Fingerprint)
        {
            return;
        }

        throw Error("Plugin selection inputs do not match the snapshot; restore and build are required.");
    }

    public static string Serialize(NantoPluginSelectionSnapshotDocument snapshot) =>
        JsonSerializer.Serialize(snapshot, NantoPluginSelectionSnapshotJsonContext.Default.NantoPluginSelectionSnapshotDocument);

    private static NantoPluginSelectionInput[] CollectInputs(string projectFile, string projectDirectory, IEnumerable<string> restoreInputs)
    {
        var paths = new HashSet<string>(_pathComparer) { projectFile };
        foreach (string input in restoreInputs.Where(static input => !string.IsNullOrWhiteSpace(input)))
        {
            paths.Add(Path.GetFullPath(input));
        }

        AddAncestorFile(paths, projectDirectory, "global.json", stopAfterFirst: true);
        AddAncestorFile(paths, projectDirectory, "Directory.Build.props", stopAfterFirst: true);
        AddAncestorFile(paths, projectDirectory, "Directory.Build.targets", stopAfterFirst: true);
        AddAncestorFile(paths, projectDirectory, "Directory.Packages.props", stopAfterFirst: true);
        AddAncestorFile(paths, projectDirectory, "NuGet.Config", stopAfterFirst: false);
        AddAncestorFile(paths, projectDirectory, "nuget.config", stopAfterFirst: false);
        string lockFile = Path.Combine(projectDirectory, "packages.lock.json");
        if (File.Exists(lockFile))
        {
            paths.Add(lockFile);
        }

        return paths.Where(File.Exists)
            .Select(path => CreateInput(path, projectDirectory))
            .OrderBy(static input => input.Identity, StringComparer.Ordinal)
            .ThenBy(static input => input.Sha256, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ReadAssetsRestoreInputs(string assetsFile)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(assetsFile), new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.TryGetProperty("project", out JsonElement project)
                && project.TryGetProperty("restore", out JsonElement restore)
                && restore.TryGetProperty("configFilePaths", out JsonElement configPaths)
                && configPaths.ValueKind == JsonValueKind.Array)
            {
                return configPaths.EnumerateArray()
                    .Where(static path => path.ValueKind == JsonValueKind.String)
                    .Select(static path => path.GetString()!)
                    .ToArray();
            }

            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _ = exception;
            throw Error("Restore metadata could not be read from project.assets.json.");
        }
    }

    private static void AddAncestorFile(HashSet<string> paths, string startDirectory, string fileName, bool stopAfterFirst)
    {
        for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                paths.Add(candidate);
                if (stopAfterFirst)
                {
                    return;
                }
            }
        }
    }

    private static NantoPluginSelectionInput CreateInput(string path, string projectDirectory)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = ReadStableFile(fullPath, "restore input");
        return new NantoPluginSelectionInput
        {
            Identity = CreatePortableIdentity(fullPath, projectDirectory, bytes),
            Path = fullPath,
            Sha256 = Hash(bytes),
        };
    }

    private static string CreatePortableIdentity(string path, string projectDirectory, byte[] bytes)
    {
        string relative = Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');
        if (!Path.IsPathRooted(relative) && !relative.StartsWith("../", StringComparison.Ordinal) && relative != "..")
        {
            return "project:" + relative;
        }

        string normalized = path.Replace('\\', '/');
        int packages = normalized.IndexOf("/.nuget/packages/", StringComparison.OrdinalIgnoreCase);
        if (packages >= 0)
        {
            return "nuget:" + normalized[(packages + "/.nuget/packages/".Length)..].ToLowerInvariant();
        }

        int sdk = normalized.IndexOf("/dotnet/sdk/", StringComparison.OrdinalIgnoreCase);
        if (sdk >= 0)
        {
            return "dotnet-sdk:" + normalized[(sdk + "/dotnet/sdk/".Length)..];
        }

        return "external:" + Path.GetFileName(path) + ":" + Hash(bytes);
    }

    private static string NormalizeRestoreProperty(string name, string value, string projectDirectory)
    {
        if (name is not ("RestoreAdditionalProjectSources" or "RestoreConfigFile" or "RestorePackagesPath" or "RestoreSources"))
        {
            return value;
        }

        return string.Join(';', value.Split(';').Select(part => NormalizeRestorePath(part, projectDirectory)));
    }

    private static string NormalizeRestorePath(string value, string projectDirectory)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            return trimmed;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed, projectDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            _ = exception;
            return trimmed;
        }

        string relative = Path.GetRelativePath(projectDirectory, fullPath).Replace('\\', '/');
        if (!Path.IsPathRooted(relative) && !relative.StartsWith("../", StringComparison.Ordinal) && relative != "..")
        {
            return "$project/" + relative;
        }

        string pathIdentity = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        return "$external/" + Path.GetFileName(fullPath) + ":" + Hash(Encoding.UTF8.GetBytes(pathIdentity));
    }

    private static byte[] Canonicalize(
        string hostProject,
        string targetFramework,
        string configuration,
        string assetsHash,
        string catalogFingerprint,
        IEnumerable<NantoPluginSelectionProperty> properties,
        IEnumerable<NantoPluginSelectionInput> inputs)
    {
        var builder = new StringBuilder();
        builder.Append("v1\nproject=").Append(hostProject).Append("\ntfm=").Append(targetFramework)
            .Append("\nconfiguration=").Append(configuration).Append("\nassets=").Append(assetsHash)
            .Append("\ncatalog=").Append(catalogFingerprint).Append('\n');
        foreach (NantoPluginSelectionProperty property in properties)
        {
            builder.Append("property=").Append(property.Name).Append('|').Append(property.ValueSha256).Append('\n');
        }

        foreach (NantoPluginSelectionInput input in inputs)
        {
            builder.Append("input=").Append(input.Identity).Append('|').Append(input.Sha256).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] ReadStableFile(string path, string description)
    {
        try
        {
            var file = new FileInfo(path);
            long length = file.Length;
            DateTime writeTime = file.LastWriteTimeUtc;
            byte[] bytes = File.ReadAllBytes(path);
            file.Refresh();
            if (bytes.LongLength != length || file.Length != length || file.LastWriteTimeUtc != writeTime)
            {
                throw Error($"The {description} changed while it was inspected.");
            }

            return bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
            throw Error($"The {description} could not be read.");
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static NantoPluginManifestException Error(string message) => new("NANTO4115", "plugin-selection", "$", message);
}