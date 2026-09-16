using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nanto.Sdk.Plugins;

internal static class NantoPluginManifestCompiler
{
    internal const int MaximumManifestBytes = 256 * 1024;
    internal const int MaximumScopeSchemaBytes = 256 * 1024;
    internal const int MaximumFrontendModuleBytes = 4 * 1024 * 1024;
    private const int MaximumIdentifierLength = 128;
    private const int MaximumReasonLength = 64;
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    public static NantoPluginCatalogDocument Compile(IEnumerable<NantoPluginManifestInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        NantoPluginManifestInput[] materializedInputs = inputs.ToArray();
        NantoPluginCatalogEntry[] plugins = materializedInputs.Select(Parse).ToArray();
        EnsureUnique(plugins, static plugin => plugin.Id, "NANTO4108", "duplicate plugin identifier");
        EnsureUnique(plugins, static plugin => plugin.PackageId, "NANTO4109", "duplicate plugin package");
        EnsureUniquePermissionMembers(plugins);

        var byId = plugins.ToDictionary(static plugin => plugin.Id, StringComparer.Ordinal);
        var byPackage = plugins.ToDictionary(static plugin => plugin.PackageId, StringComparer.OrdinalIgnoreCase);
        var inputByPackage = materializedInputs.ToDictionary(static input => input.PackageId, StringComparer.OrdinalIgnoreCase);
        foreach (NantoPluginCatalogEntry plugin in plugins)
        {
            foreach (string dependencyId in plugin.Dependencies)
            {
                if (!byId.TryGetValue(dependencyId, out NantoPluginCatalogEntry? dependency))
                {
                    throw Error("NANTO4110", plugin.PackageId, "$.dependencies", $"Plugin dependency '{dependencyId}' is not selected.");
                }

                if (!inputByPackage[plugin.PackageId].PackageDependencies.ContainsKey(dependency.PackageId))
                {
                    throw Error("NANTO4111", plugin.PackageId, "$.dependencies", $"Plugin dependency '{dependencyId}' is absent from the evaluated NuGet dependency graph.");
                }
            }

            var declaredPackages = plugin.Dependencies.Select(dependency => byId[dependency].PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? extraDependency = inputByPackage[plugin.PackageId].PackageDependencies.Keys
                .Where(byPackage.ContainsKey)
                .FirstOrDefault(package => !declaredPackages.Contains(package));
            if (extraDependency is not null)
            {
                throw Error("NANTO4112", plugin.PackageId, "$.dependencies", $"Selected plugin package dependency '{extraDependency}' is not declared as a plugin dependency.");
            }
        }

        NantoPluginCatalogEntry[] ordered = TopologicalSort(plugins, byId);
        string fingerprint = Hash(CanonicalizeCatalog(ordered));
        return new NantoPluginCatalogDocument { Fingerprint = fingerprint, Plugins = ordered };
    }

    public static string Serialize(NantoPluginCatalogDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return JsonSerializer.Serialize(catalog, NantoPluginCatalogJsonContext.Default.NantoPluginCatalogDocument);
    }

    private static NantoPluginCatalogEntry Parse(NantoPluginManifestInput input)
    {
        byte[] bytes = ReadManifest(input);
        string text;
        try
        {
            text = _strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Error("NANTO4101", input.DisplayPath, "$", "Plugin manifest is not valid UTF-8.");
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            throw Error("NANTO4101", input.DisplayPath, "$", "Plugin manifest must be UTF-8 without a byte-order mark.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            RequireObject(root, input.DisplayPath, "$", ["schemaVersion", "id", "catalogFingerprint", "runtimeCompatibility", "dependencies", "permissions"], ["frontendModule"]);
            RequireInteger(root, "schemaVersion", input.DisplayPath, "$", 1);
            string id = RequireString(root, "id", input.DisplayPath, "$", MaximumIdentifierLength);
            if (!IsPluginIdentifier(id) || id == "app")
            {
                throw Error("NANTO4103", input.DisplayPath, "$.id", "Plugin identifier must contain lowercase ASCII dot-separated segments and must not use the reserved 'app' namespace.");
            }

            string claimedFingerprint = RequireHash(root, "catalogFingerprint", input.DisplayPath, "$");
            RuntimeCompatibility compatibility = ParseCompatibility(root.GetProperty("runtimeCompatibility"), input);
            string[] dependencies = ReadUniqueStrings(root.GetProperty("dependencies"), input.DisplayPath, "$.dependencies", IsPluginIdentifier, MaximumIdentifierLength);
            NantoPluginPermissionEntry[] permissions = ParsePermissions(root.GetProperty("permissions"), input, id);
            string actualFingerprint = Hash(CanonicalizePermissions(permissions));
            if (!string.Equals(claimedFingerprint, actualFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw Error("NANTO4107", input.DisplayPath, "$.catalogFingerprint", "Plugin permission catalog fingerprint does not match the declared permission/member catalog.");
            }

            string? frontendModule = null;
            string? frontendModuleSha256 = null;
            if (root.TryGetProperty("frontendModule", out JsonElement frontendProperty))
            {
                frontendModule = RequireRelativeFile(frontendProperty, input, "$.frontendModule");
                frontendModuleSha256 = Hash(ReadStableFile(
                    Path.Combine(input.PackageRoot, frontendModule.Replace('/', Path.DirectorySeparatorChar)),
                    input.DisplayPath,
                    "$.frontendModule",
                    MaximumFrontendModuleBytes));
            }

            return new NantoPluginCatalogEntry
            {
                Id = id,
                PackageId = input.PackageId,
                PackageVersion = input.PackageVersion,
                RuntimeCompatibility = compatibility.Mode,
                IncompatibleDependencyPackage = compatibility.DependencyPackage,
                IncompatibleDependencyVersion = compatibility.DependencyVersion,
                CompatibilityReason = compatibility.Reason,
                Dependencies = dependencies,
                Permissions = permissions,
                FrontendModule = frontendModule,
                FrontendModuleSha256 = frontendModuleSha256,
                ManifestSha256 = Hash(bytes),
                CatalogFingerprint = actualFingerprint,
            };
        }
        catch (JsonException exception)
        {
            throw Error("NANTO4102", input.DisplayPath, "$", $"Plugin manifest is malformed JSON at line {exception.LineNumber ?? 0}, byte {exception.BytePositionInLine ?? 0}.");
        }
    }

    private static byte[] ReadManifest(NantoPluginManifestInput input)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.PackageRoot));
            var file = new FileInfo(input.ManifestPath);
            if (!file.Exists)
            {
                throw Error("NANTO4100", input.DisplayPath, "$", "Plugin manifest does not exist.");
            }

            string fullPath = Path.GetFullPath(file.FullName);
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw Error("NANTO4100", input.DisplayPath, "$", "Plugin manifest escapes the package root.");
            }

            ThrowIfReparsePointTree(root, file, input.DisplayPath, "$");

            if (file.Length is <= 0 or > MaximumManifestBytes)
            {
                throw Error("NANTO4100", input.DisplayPath, "$", $"Plugin manifest must contain between 1 and {MaximumManifestBytes} bytes.");
            }

            return ReadStableFile(file.FullName, input.DisplayPath, "$", MaximumManifestBytes);
        }
        catch (IOException)
        {
            throw Error("NANTO4100", input.DisplayPath, "$", "Plugin manifest could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            throw Error("NANTO4100", input.DisplayPath, "$", "Plugin manifest could not be read.");
        }
    }

    private static NantoPluginPermissionEntry[] ParsePermissions(JsonElement element, NantoPluginManifestInput input, string pluginId)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error("NANTO4104", input.DisplayPath, "$.permissions", "Plugin permissions must be an array.");
        }

        var permissions = new List<NantoPluginPermissionEntry>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (JsonElement permission in element.EnumerateArray())
        {
            string path = $"$.permissions[{index}]";
            RequireObject(permission, input.DisplayPath, path, ["identifier", "members"], ["scopeSchema"]);
            string identifier = RequireString(permission, "identifier", input.DisplayPath, path, MaximumIdentifierLength);
            if (!IsPermissionIdentifier(identifier, out string permissionPluginId))
            {
                throw Error("NANTO4105", input.DisplayPath, path + ".identifier", "Permission identifier must use the plugin namespace and lowercase ASCII dot-separated names.");
            }

            if (!string.Equals(pluginId, permissionPluginId, StringComparison.Ordinal))
            {
                throw Error("NANTO4105", input.DisplayPath, path + ".identifier", "Permission identifier namespace must equal the plugin identifier.");
            }

            if (!identifiers.Add(identifier))
            {
                throw Error("NANTO4106", input.DisplayPath, path + ".identifier", "Plugin permission identifier is duplicated.");
            }

            string[] permissionMembers = ReadUniqueStrings(permission.GetProperty("members"), input.DisplayPath, path + ".members", IsBridgeMember, MaximumIdentifierLength);
            if (permissionMembers.Length == 0)
            {
                throw Error("NANTO4104", input.DisplayPath, path + ".members", "Plugin permission members must not be empty.");
            }

            foreach (string member in permissionMembers)
            {
                if (!members.Add(member))
                {
                    throw Error("NANTO4106", input.DisplayPath, path + ".members", $"Bridge member '{member}' is assigned to more than one plugin permission.");
                }
            }

            string? scopeSchema = null;
            string? scopeSchemaSha256 = null;
            if (permission.TryGetProperty("scopeSchema", out JsonElement scopeProperty))
            {
                scopeSchema = RequireRelativeFile(scopeProperty, input, path + ".scopeSchema");
                scopeSchemaSha256 = Hash(ReadStableFile(
                    Path.Combine(input.PackageRoot, scopeSchema.Replace('/', Path.DirectorySeparatorChar)),
                    input.DisplayPath,
                    path + ".scopeSchema",
                    MaximumScopeSchemaBytes));
            }

            permissions.Add(new NantoPluginPermissionEntry
            {
                Identifier = identifier,
                Members = permissionMembers,
                ScopeSchema = scopeSchema,
                ScopeSchemaSha256 = scopeSchemaSha256,
            });
            index++;
        }

        return [.. permissions.OrderBy(static permission => permission.Identifier, StringComparer.Ordinal)];
    }

    private static RuntimeCompatibility ParseCompatibility(JsonElement element, NantoPluginManifestInput input)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() == "nativeAot")
        {
            return new RuntimeCompatibility("nativeAot", null, null, null);
        }

        RequireObject(element, input.DisplayPath, "$.runtimeCompatibility", ["mode", "dependencyPackage", "dependencyVersion", "reason"], []);
        string mode = RequireString(element, "mode", input.DisplayPath, "$.runtimeCompatibility", 16);
        string dependencyPackage = RequireString(element, "dependencyPackage", input.DisplayPath, "$.runtimeCompatibility", MaximumIdentifierLength);
        string dependencyVersion = RequireString(element, "dependencyVersion", input.DisplayPath, "$.runtimeCompatibility", MaximumIdentifierLength);
        string reason = RequireString(element, "reason", input.DisplayPath, "$.runtimeCompatibility", MaximumReasonLength);
        if (mode != "coreClr"
            || !IsNuGetIdentity(dependencyPackage)
            || dependencyVersion.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character))
            || !IsBoundedReason(reason))
        {
            throw Error("NANTO4104", input.DisplayPath, "$.runtimeCompatibility", "CoreCLR compatibility must name an exact dependency package/version and a bounded symbolic reason.");
        }

        bool namesPluginPackage = dependencyPackage.Equals(input.PackageId, StringComparison.OrdinalIgnoreCase)
            && dependencyVersion.Equals(input.PackageVersion, StringComparison.Ordinal);
        bool namesSelectedDependency = input.PackageDependencies.TryGetValue(dependencyPackage, out string? selectedVersion)
            && dependencyVersion.Equals(selectedVersion, StringComparison.Ordinal);
        if (!namesPluginPackage && !namesSelectedDependency)
        {
            throw Error("NANTO4104", input.DisplayPath, "$.runtimeCompatibility", "CoreCLR compatibility dependency must identify the plugin package or one exact selected package dependency.");
        }

        return new RuntimeCompatibility(mode, dependencyPackage, dependencyVersion, reason);
    }

    private static string RequireRelativeFile(JsonElement element, NantoPluginManifestInput input, string propertyPath)
    {
        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } relative
            || relative.Length > 256
            || !IsPackageRelativePath(relative))
        {
            throw Error("NANTO4104", input.DisplayPath, propertyPath, "Plugin asset path must be a normalized package-relative path.");
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.PackageRoot));
        string fullPath = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), root);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !input.PackageFiles.Contains(relative)
            || !File.Exists(fullPath))
        {
            throw Error("NANTO4104", input.DisplayPath, propertyPath, "Plugin asset path is not a declared package file or escapes the package root.");
        }

        ThrowIfReparsePointTree(root, new FileInfo(fullPath), input.DisplayPath, propertyPath);

        return relative;
    }

    private static void RequireObject(JsonElement element, string document, string path, string[] required, string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error("NANTO4104", document, path, "Expected a JSON object.");
        }

        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Error("NANTO4104", document, path + "." + property.Name, "Unknown plugin manifest property.");
            }

            if (!seen.Add(property.Name))
            {
                throw Error("NANTO4104", document, path + "." + property.Name, "Duplicate plugin manifest property.");
            }
        }

        string? missing = required.FirstOrDefault(property => !seen.Contains(property));
        if (missing is not null)
        {
            throw Error("NANTO4104", document, path + "." + missing, "Required plugin manifest property is missing.");
        }
    }

    private static string RequireString(JsonElement owner, string name, string document, string path, int maximumLength)
    {
        JsonElement element = owner.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: > 0 } value || value.Length > maximumLength)
        {
            throw Error("NANTO4104", document, path + "." + name, $"Property must be a non-empty string no longer than {maximumLength} characters.");
        }

        return value;
    }

    private static void RequireInteger(JsonElement owner, string name, string document, string path, int expected)
    {
        JsonElement element = owner.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value) || value != expected)
        {
            throw Error("NANTO4104", document, path + "." + name, $"Property must equal {expected}.");
        }
    }

    private static string RequireHash(JsonElement owner, string name, string document, string path)
    {
        string value = RequireString(owner, name, document, path, 64);
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw Error("NANTO4104", document, path + "." + name, "Property must be a 64-character SHA-256 hexadecimal value.");
        }

        return value.ToUpperInvariant();
    }

    private static string[] ReadUniqueStrings(JsonElement element, string document, string path, Func<string, bool> validator, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error("NANTO4104", document, path, "Property must be an array.");
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not { Length: > 0 } value
                || value.Length > maximumLength
                || !validator(value))
            {
                throw Error("NANTO4104", document, $"{path}[{index}]", "Array item is invalid.");
            }

            if (!values.Add(value))
            {
                throw Error("NANTO4106", document, $"{path}[{index}]", "Array item is duplicated.");
            }

            index++;
        }

        return [.. values.Order(StringComparer.Ordinal)];
    }

    private static byte[] ReadStableFile(string path, string document, string propertyPath, long maximumBytes = long.MaxValue)
    {
        try
        {
            var file = new FileInfo(path);
            long initialLength = file.Length;
            DateTime initialWrite = file.LastWriteTimeUtc;
            if (initialLength > maximumBytes)
            {
                throw Error("NANTO4104", document, propertyPath, $"Plugin file exceeds the {maximumBytes}-byte limit.");
            }

            byte[] bytes;
            using (FileStream stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bytes = new byte[initialLength];
                stream.ReadExactly(bytes);
            }

            file.Refresh();
            if (file.Length != initialLength || file.LastWriteTimeUtc != initialWrite)
            {
                throw Error("NANTO4104", document, propertyPath, "Plugin file changed while it was inspected.");
            }

            return bytes;
        }
        catch (IOException)
        {
            throw Error("NANTO4104", document, propertyPath, "Plugin file could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            throw Error("NANTO4104", document, propertyPath, "Plugin file could not be read.");
        }
    }

    private static void ThrowIfReparsePointTree(string root, FileSystemInfo leaf, string document, string propertyPath)
    {
        FileSystemInfo? current = leaf;
        while (current is not null && !string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Error("NANTO4104", document, propertyPath, "Plugin path must not traverse a reparse point.");
            }

            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }

        var rootDirectory = new DirectoryInfo(root);
        if ((rootDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Error("NANTO4104", document, propertyPath, "Plugin package root must not be a reparse point.");
        }
    }

    private static NantoPluginCatalogEntry[] TopologicalSort(NantoPluginCatalogEntry[] plugins, Dictionary<string, NantoPluginCatalogEntry> byId)
    {
        var remainingDependencies = plugins.ToDictionary(static plugin => plugin.Id, static plugin => plugin.Dependencies.Length, StringComparer.Ordinal);
        var dependents = plugins.ToDictionary(static plugin => plugin.Id, static _ => new List<string>(), StringComparer.Ordinal);
        foreach (NantoPluginCatalogEntry plugin in plugins)
        {
            foreach (string dependency in plugin.Dependencies)
            {
                dependents[dependency].Add(plugin.Id);
            }
        }

        var ready = new SortedSet<string>(remainingDependencies.Where(static pair => pair.Value == 0).Select(static pair => pair.Key), StringComparer.Ordinal);
        var ordered = new List<NantoPluginCatalogEntry>(plugins.Length);
        while (ready.Count > 0)
        {
            string id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (string dependent in dependents[id])
            {
                remainingDependencies[dependent]--;
                if (remainingDependencies[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (ordered.Count != plugins.Length)
        {
            throw Error("NANTO4113", "plugin-catalog", "$.dependencies", "Plugin dependency graph contains a cycle.");
        }

        return [.. ordered];
    }

    private static void EnsureUnique(NantoPluginCatalogEntry[] plugins, Func<NantoPluginCatalogEntry, string> selector, string code, string message)
    {
        if (plugins.GroupBy(selector, StringComparer.OrdinalIgnoreCase).FirstOrDefault(static group => group.Count() > 1) is { } duplicate)
        {
            throw Error(code, duplicate.First().PackageId, "$", $"Plugin catalog contains a {message} '{duplicate.Key}'.");
        }
    }

    private static void EnsureUniquePermissionMembers(NantoPluginCatalogEntry[] plugins)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (NantoPluginCatalogEntry plugin in plugins.OrderBy(static plugin => plugin.Id, StringComparer.Ordinal))
        {
            foreach (NantoPluginPermissionEntry permission in plugin.Permissions)
            {
                foreach (string member in permission.Members)
                {
                    if (!owners.TryAdd(member, permission.Identifier))
                    {
                        throw Error(
                            "NANTO4106",
                            plugin.PackageId,
                            "$.permissions",
                            $"Bridge member '{member}' is assigned to both permission '{owners[member]}' and '{permission.Identifier}'.");
                    }
                }
            }
        }
    }

    private static byte[] CanonicalizePermissions(IEnumerable<NantoPluginPermissionEntry> permissions)
    {
        var builder = new StringBuilder();
        foreach (NantoPluginPermissionEntry permission in permissions.OrderBy(static permission => permission.Identifier, StringComparer.Ordinal))
        {
            builder.Append(permission.Identifier).Append('\n');
            foreach (string member in permission.Members.Order(StringComparer.Ordinal))
            {
                builder.Append("member=").Append(member).Append('\n');
            }

            if (permission.ScopeSchema is not null)
            {
                builder.Append("scope=").Append(permission.ScopeSchema).Append('|').Append(permission.ScopeSchemaSha256).Append('\n');
            }
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] CanonicalizeCatalog(IEnumerable<NantoPluginCatalogEntry> plugins)
    {
        var builder = new StringBuilder();
        foreach (NantoPluginCatalogEntry plugin in plugins)
        {
            builder.Append(plugin.Id).Append('|').Append(plugin.PackageId).Append('|').Append(plugin.PackageVersion).Append('|')
                .Append(plugin.RuntimeCompatibility).Append('|').Append(plugin.IncompatibleDependencyPackage).Append('|')
                .Append(plugin.IncompatibleDependencyVersion).Append('|').Append(plugin.CompatibilityReason).Append('|')
                .Append(plugin.CatalogFingerprint).Append('|').Append(plugin.ManifestSha256).Append('|')
                .Append(plugin.FrontendModule).Append('|').Append(plugin.FrontendModuleSha256).Append('\n');
            foreach (string dependency in plugin.Dependencies)
            {
                builder.Append("dependency=").Append(dependency).Append('\n');
            }
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static bool IsPermissionIdentifier(string value, out string pluginId)
    {
        int separator = value.IndexOf(':');
        pluginId = separator > 0 ? value[..separator] : string.Empty;
        return separator > 0 && IsPluginIdentifier(pluginId) && IsLowerDotName(value[(separator + 1)..]);
    }

    private static bool IsPluginIdentifier(string value) => IsLowerDotName(value);

    private static bool IsLowerDotName(string value) => value.Length > 0 && value.Split('.').All(static segment =>
        segment.Length > 0 && segment[0] is >= 'a' and <= 'z' && segment.All(static character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character)));

    private static bool IsBridgeMember(string value) => value.Length > 0 && value.Split('.').All(static segment =>
        segment.Length > 0 && char.IsAsciiLetter(segment[0]) && segment.All(static character => char.IsAsciiLetterOrDigit(character)));

    private static bool IsPackageRelativePath(string value) => value.Length > 0
        && !Path.IsPathRooted(value)
        && !value.Contains('\\')
        && !value.Contains(':')
        && value.Split('/').All(static segment => segment.Length > 0 && segment is not "." and not ".." && segment == segment.Normalize(NormalizationForm.FormC));

    private static bool IsNuGetIdentity(string value) => value.Length > 0 && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');

    private static bool IsBoundedReason(string value) => value.Length <= MaximumReasonLength
        && value[0] is >= 'a' and <= 'z'
        && value.All(static character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character is '.' or '-');

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static NantoPluginManifestException Error(string code, string document, string propertyPath, string message) =>
        new(code, document, propertyPath, message);

    private sealed record RuntimeCompatibility(string Mode, string? DependencyPackage, string? DependencyVersion, string? Reason);
}
