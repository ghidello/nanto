using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Nanto.Sdk.Plugins;

namespace Nanto.Generators.Tests;

public sealed class NantoPluginManifestCompilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nanto-plugin-manifest-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CompileOrdersDependenciesAndProducesStableCatalog()
    {
        NantoPluginManifestInput dependency = WriteManifest(
            "Nanto.Plugin.Dependency",
            "1.2.3",
            "fixture.dependency",
            permissions: "[]");
        const string permissions = """
            [{
              "identifier": "fixture.filesystem:read",
              "members": ["fixtureFilesystem.read"]
            }]
            """;
        NantoPluginManifestInput filesystem = WriteManifest(
            "Nanto.Plugin.Filesystem",
            "2.0.0",
            "fixture.filesystem",
            permissions,
            dependencies: "[\"fixture.dependency\"]",
            packageDependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Nanto.Plugin.Dependency"] = "1.2.3" });

        NantoPluginCatalogDocument first = NantoPluginManifestCompiler.Compile([filesystem, dependency]);
        NantoPluginCatalogDocument second = NantoPluginManifestCompiler.Compile([dependency, filesystem]);

        first.Plugins.Select(static plugin => plugin.Id).Should().Equal("fixture.dependency", "fixture.filesystem");
        first.Fingerprint.Should().Be(second.Fingerprint);
        NantoPluginManifestCompiler.Serialize(first).Should().Be(NantoPluginManifestCompiler.Serialize(second));
    }

    [Theory]
    [InlineData("\"unknown\": true,", "NANTO4104", null)]
    [InlineData("", "NANTO4107", "0A00000000000000000000000000000000000000000000000000000000000000")]
    public void CompileRejectsMalformedOrMismatchedManifest(string extraProperty, string expectedCode, string? fingerprint = null)
    {
        NantoPluginManifestInput input = WriteManifest(
            "Nanto.Plugin.Invalid",
            "1.0.0",
            "fixture.invalid",
            permissions: "[]",
            extraProperty: extraProperty,
            catalogFingerprint: fingerprint);

        var action = () => NantoPluginManifestCompiler.Compile([input]);

        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void CompileRejectsPermissionFromAnotherPluginNamespace()
    {
        const string permissions = """
            [{
              "identifier": "fixture.other:read",
              "members": ["fixtureFilesystem.read"]
            }]
            """;
        NantoPluginManifestInput input = WriteManifest("Nanto.Plugin.Filesystem", "1.0.0", "fixture.filesystem", permissions);

        var action = () => NantoPluginManifestCompiler.Compile([input]);

        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be("NANTO4105");
    }

    [Fact]
    public void CompileRejectsReservedApplicationPluginIdentifier()
    {
        NantoPluginManifestInput input = WriteManifest("Nanto.Plugin.Invalid", "1.0.0", "app", "[]");

        var action = () => NantoPluginManifestCompiler.Compile([input]);

        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be("NANTO4103");
    }

    [Fact]
    public void CompileRejectsPluginDependencyMissingFromNuGetGraph()
    {
        NantoPluginManifestInput dependency = WriteManifest("Nanto.Plugin.Dependency", "1.0.0", "fixture.dependency", "[]");
        NantoPluginManifestInput dependent = WriteManifest(
            "Nanto.Plugin.Dependent",
            "1.0.0",
            "fixture.dependent",
            "[]",
            dependencies: "[\"fixture.dependency\"]");

        var action = () => NantoPluginManifestCompiler.Compile([dependency, dependent]);

        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be("NANTO4111");
    }

    [Fact]
    public void CompileRejectsSelectedPluginPackageDependencyMissingFromManifest()
    {
        NantoPluginManifestInput dependency = WriteManifest("Nanto.Plugin.Dependency", "1.0.0", "fixture.dependency", "[]");
        NantoPluginManifestInput dependent = WriteManifest(
            "Nanto.Plugin.Dependent",
            "1.0.0",
            "fixture.dependent",
            "[]",
            packageDependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Nanto.Plugin.Dependency"] = "1.0.0" });

        var action = () => NantoPluginManifestCompiler.Compile([dependency, dependent]);

        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be("NANTO4112");
    }

    [Fact]
    public void CompileRejectsPackageRelativeAssetEscape()
    {
        NantoPluginManifestInput input = WriteManifest(
            "Nanto.Plugin.Invalid",
            "1.0.0",
            "fixture.invalid",
            "[]",
            extraProperty: "\"frontendModule\": \"../outside.js\",");

        var action = () => NantoPluginManifestCompiler.Compile([input]);

        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be("NANTO4104");
    }

    [Fact]
    public void CompileRejectsDependencyCycle()
    {
        NantoPluginManifestInput first = WriteManifest(
            "Nanto.Plugin.First",
            "1.0.0",
            "fixture.first",
            "[]",
            dependencies: "[\"fixture.second\"]",
            packageDependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Nanto.Plugin.Second"] = "1.0.0" });
        NantoPluginManifestInput second = WriteManifest(
            "Nanto.Plugin.Second",
            "1.0.0",
            "fixture.second",
            "[]",
            dependencies: "[\"fixture.first\"]",
            packageDependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Nanto.Plugin.First"] = "1.0.0" });

        var action = () => NantoPluginManifestCompiler.Compile([first, second]);

        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be("NANTO4113");
    }

    [Fact]
    public void CompileRejectsBridgeMemberOwnedByTwoPlugins()
    {
        const string firstPermissions = """
            [{
              "identifier": "fixture.first:read",
              "members": ["fixtureShared.read"]
            }]
            """;
        const string secondPermissions = """
            [{
              "identifier": "fixture.second:read",
              "members": ["fixtureShared.read"]
            }]
            """;
        NantoPluginManifestInput first = WriteManifest("Nanto.Plugin.First", "1.0.0", "fixture.first", firstPermissions);
        NantoPluginManifestInput second = WriteManifest("Nanto.Plugin.Second", "1.0.0", "fixture.second", secondPermissions);

        var action = () => NantoPluginManifestCompiler.Compile([first, second]);

        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be("NANTO4106");
    }

    [Fact]
    public void CompileRequiresCoreClrCompatibilityToNameExactSelectedPackageVersion()
    {
        const string compatibility = """
            {
              "mode": "coreClr",
              "dependencyPackage": "Nanto.Native.Dependency",
              "dependencyVersion": "2.0.0",
              "reason": "requires-coreclr"
            }
            """;
        NantoPluginManifestInput input = WriteManifest(
            "Nanto.Plugin.CoreClr",
            "1.0.0",
            "fixture.coreclr",
            "[]",
            runtimeCompatibility: compatibility,
            packageDependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Nanto.Native.Dependency"] = "2.0.1" });

        var action = () => NantoPluginManifestCompiler.Compile([input]);

        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be("NANTO4104");
    }

    [Fact]
    public void SelectionReaderUsesExactPackageVersionsAndSelectedDependencyEdges()
    {
        string packageFolder = Path.Combine(_root, "packages");
        string packageRoot = Path.Combine(packageFolder, "nanto.plugin.filesystem", "2.0.0");
        string manifestPath = Path.Combine(packageRoot, "nanto", "plugin-manifest-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{}");
        string assetsPath = Path.Combine(_root, "project.assets.json");
        string normalizedPackageFolder = packageFolder.Replace("\\", "\\\\") + "\\\\";
        File.WriteAllText(
            assetsPath,
            $$"""
            {
              "targets": {
                "net10.0": {
                  "Nanto.Plugin.Filesystem/2.0.0": {
                    "type": "package",
                    "dependencies": { "Nanto.Plugin.Dependency": "[1.2.3, )" }
                  },
                  "Nanto.Plugin.Dependency/1.2.3": { "type": "package" }
                },
                "net9.0": {
                  "Nanto.Plugin.Dependency/1.0.0": { "type": "package" }
                }
              },
              "libraries": {
                "Nanto.Plugin.Filesystem/2.0.0": {
                  "type": "package",
                  "path": "nanto.plugin.filesystem/2.0.0",
                  "files": ["nanto/plugin-manifest-v1.json"]
                },
                "Nanto.Plugin.Dependency/1.2.3": {
                  "type": "package",
                  "path": "nanto.plugin.dependency/1.2.3",
                  "files": []
                },
                "Nanto.Plugin.Dependency/1.0.0": {
                  "type": "package",
                  "path": "nanto.plugin.dependency/1.0.0",
                  "files": []
                }
              },
              "packageFolders": { "{{normalizedPackageFolder}}": {} }
            }
            """);

        NantoPluginManifestInput input = NantoPluginSelectionReader.Read(
            assetsPath,
            "net10.0",
            [new NantoPluginManifestReference(manifestPath, "Nanto.Plugin.Filesystem")]).Should().ContainSingle().Which;

        input.PackageId.Should().Be("Nanto.Plugin.Filesystem");
        input.PackageVersion.Should().Be("2.0.0");
        input.PackageDependencies.Should().Contain("Nanto.Plugin.Dependency", "1.2.3");
    }

    [Fact]
    public void SelectionReaderRejectsManifestOutsideFrozenPackagePath()
    {
        string packageFolder = Path.Combine(_root, "packages");
        string packageRoot = Path.Combine(packageFolder, "nanto.plugin.filesystem", "2.0.0");
        string manifestPath = Path.Combine(packageRoot, "metadata", "plugin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{}");
        string assetsPath = Path.Combine(_root, "project.assets.json");
        string normalizedPackageFolder = packageFolder.Replace("\\", "\\\\") + "\\\\";
        File.WriteAllText(
            assetsPath,
            $$"""
            {
              "targets": {
                "net10.0": {
                  "Nanto.Plugin.Filesystem/2.0.0": { "type": "package" }
                }
              },
              "libraries": {
                "Nanto.Plugin.Filesystem/2.0.0": {
                  "type": "package",
                  "path": "nanto.plugin.filesystem/2.0.0",
                  "files": ["metadata/plugin.json"]
                }
              },
              "packageFolders": { "{{normalizedPackageFolder}}": {} }
            }
            """);

        var action = () => NantoPluginSelectionReader.Read(
            assetsPath,
            "net10.0",
            [new NantoPluginManifestReference(manifestPath, "Nanto.Plugin.Filesystem")]);

        action.Should().Throw<NantoPluginManifestException>()
            .Which.Message.Should().Contain("nanto/plugin-manifest-v1.json");
    }

    [Fact]
    public void SelectionSnapshotIsDeterministicAndRejectsChangedInputsWithoutRestore()
    {
        string projectDirectory = Path.Combine(_root, "snapshot");
        Directory.CreateDirectory(projectDirectory);
        string projectFile = Path.Combine(projectDirectory, "App.csproj");
        string propsFile = Path.Combine(projectDirectory, "Directory.Build.props");
        string assetsFile = Path.Combine(projectDirectory, "project.assets.json");
        File.WriteAllText(projectFile, "<Project />");
        File.WriteAllText(propsFile, "<Project />");
        File.WriteAllText(assetsFile, "{}");
        NantoPluginCatalogDocument catalog = NantoPluginManifestCompiler.Compile([]);

        NantoPluginSelectionSnapshotDocument first = NantoPluginSelectionSnapshotCompiler.Create(
            assetsFile,
            projectFile,
            "net10.0",
            "Debug",
            catalog,
            [new KeyValuePair<string, string>("TargetFramework", "net10.0")],
            [propsFile]);
        NantoPluginSelectionSnapshotDocument repeated = NantoPluginSelectionSnapshotCompiler.Create(
            assetsFile,
            projectFile,
            "net10.0",
            "Debug",
            catalog,
            [new KeyValuePair<string, string>("TargetFramework", "net10.0")],
            [propsFile]);
        string snapshotPath = Path.Combine(projectDirectory, "selection.json");
        File.WriteAllText(snapshotPath, NantoPluginSelectionSnapshotCompiler.Serialize(first));
        NantoPluginSelectionSnapshotDocument changed = NantoPluginSelectionSnapshotCompiler.Create(
            assetsFile,
            projectFile,
            "net10.0",
            "Release",
            catalog,
            [new KeyValuePair<string, string>("TargetFramework", "net10.0")],
            [propsFile]);

        first.Fingerprint.Should().Be(repeated.Fingerprint);
        var action = () => NantoPluginSelectionSnapshotCompiler.EnsureFresh(snapshotPath, changed);
        action.Should().Throw<NantoPluginManifestException>().Which.Code.Should().Be("NANTO4115");
        NantoPluginSelectionSnapshotCompiler.EnsureFresh(snapshotPath, repeated);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private NantoPluginManifestInput WriteManifest(
        string packageId,
        string packageVersion,
        string pluginId,
        string permissions,
        string dependencies = "[]",
        string extraProperty = "",
        string? catalogFingerprint = null,
        string runtimeCompatibility = "\"nativeAot\"",
        IReadOnlyDictionary<string, string>? packageDependencies = null)
    {
        string packageRoot = Path.Combine(_root, packageId, packageVersion);
        Directory.CreateDirectory(packageRoot);
        string manifestPath = Path.Combine(packageRoot, "nanto-plugin.json");
        catalogFingerprint ??= ComputePermissionFingerprint(permissions);
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "schemaVersion": 1,
              "id": "{{pluginId}}",
              "catalogFingerprint": "{{catalogFingerprint}}",
              "runtimeCompatibility": {{runtimeCompatibility}},
              "dependencies": {{dependencies}},
              {{extraProperty}}
              "permissions": {{permissions}}
            }
            """);
        return new NantoPluginManifestInput(
            manifestPath,
            $"{packageId}/nanto-plugin.json",
            packageRoot,
            packageId,
            packageVersion,
            packageDependencies ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.Ordinal) { "nanto-plugin.json" });
    }

    private static string ComputePermissionFingerprint(string permissions)
    {
        using JsonDocument document = JsonDocument.Parse(permissions);
        var canonical = new StringBuilder();
        foreach (JsonElement permission in document.RootElement.EnumerateArray()
            .OrderBy(static permission => permission.GetProperty("identifier").GetString(), StringComparer.Ordinal))
        {
            canonical.Append(permission.GetProperty("identifier").GetString()).Append('\n');
            foreach (JsonElement member in permission.GetProperty("members").EnumerateArray()
                .OrderBy(static member => member.GetString(), StringComparer.Ordinal))
            {
                canonical.Append("member=").Append(member.GetString()).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
