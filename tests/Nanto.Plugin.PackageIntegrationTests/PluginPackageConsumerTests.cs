using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text.Json;

using AwesomeAssertions;

namespace Nanto.Plugin.PackageIntegrationTests;

public sealed class PluginPackageConsumerTests
{
    [Fact]
    public async Task PackedBuildTransitiveManifestsProduceCatalogAndRejectMalformedOrCollidingPackages()
    {
        string repositoryRoot = GetAssemblyMetadata("NantoRepositoryRoot");
        string configuration = GetAssemblyMetadata("NantoConfiguration");
        string root = Path.Combine(Path.GetTempPath(), "nanto-plugin-package-tests", Guid.NewGuid().ToString("N"));
        string feed = Path.Combine(root, "feed");
        Directory.CreateDirectory(feed);
        File.Copy(Path.Combine(repositoryRoot, "global.json"), Path.Combine(root, "global.json"));
        try
        {
            await PackAsync(repositoryRoot, feed, configuration, "src/Nanto.Sdk/Nanto.Sdk.csproj");
            await PackAsync(repositoryRoot, feed, configuration, "tests/Nanto.Plugin.Fixture.Filesystem/Nanto.Plugin.Fixture.Filesystem.csproj");
            await PackAsync(repositoryRoot, feed, configuration, "tests/Nanto.Plugin.Fixture.CoreClrOnly/Nanto.Plugin.Fixture.CoreClrOnly.csproj");
            await PackAsync(repositoryRoot, feed, configuration, "tests/Nanto.Plugin.Fixture.Collision/Nanto.Plugin.Fixture.Collision.csproj");
            await PackAsync(repositoryRoot, feed, configuration, "tests/Nanto.Plugin.Fixture.Malformed/Nanto.Plugin.Fixture.Malformed.csproj");

            ProcessResult valid = await BuildConsumerAsync(root, feed, "valid", [
                "Nanto.Plugin.Fixture.Filesystem",
                "Nanto.Plugin.Fixture.CoreClrOnly",
            ]);
            valid.ExitCode.Should().Be(0, valid.Output);
            string snapshotPath = Path.Combine(root, "valid", "obj", "Debug", "net10.0", "Nanto", "Plugins", "nanto-plugin-selection-v1.json");
            using JsonDocument snapshot = JsonDocument.Parse(await File.ReadAllBytesAsync(snapshotPath, TestContext.Current.CancellationToken));
            snapshot.RootElement.GetProperty("plugins").EnumerateArray()
                .Select(static plugin => plugin.GetProperty("id").GetString())
                .Should().Equal("fixture.coreclronly", "fixture.filesystem");
            JsonElement filesystem = snapshot.RootElement.GetProperty("plugins").EnumerateArray()
                .Single(static plugin => plugin.GetProperty("id").GetString() == "fixture.filesystem");
            filesystem.GetProperty("permissions")[0].GetProperty("identifier").GetString().Should().Be("fixture.filesystem:read");
            filesystem.GetProperty("permissions")[0].GetProperty("members")[0].GetString().Should().Be("fixtureFilesystem.read");
            filesystem.GetProperty("frontendModuleSha256").GetString().Should().MatchRegex("^[A-F0-9]{64}$");
            string[] restoreInputIdentities = snapshot.RootElement.GetProperty("restoreInputs").EnumerateArray()
                .Select(static input => input.GetProperty("identity").GetString()!)
                .ToArray();
            restoreInputIdentities.Should().Contain(identity => identity.Contains("Nanto.Sdk.targets", StringComparison.Ordinal));
            restoreInputIdentities.Should().Contain(identity => identity.Contains("Nanto.Plugin.Fixture.Filesystem.props", StringComparison.Ordinal));
            restoreInputIdentities.Should().Contain(identity => identity.Contains("Nanto.Plugin.Fixture.CoreClrOnly.props", StringComparison.Ordinal));
            await AssertChangedSelectionInputRequiresRestoreAsync(root);

            ProcessResult collision = await BuildConsumerAsync(root, feed, "collision", [
                "Nanto.Plugin.Fixture.Filesystem",
                "Nanto.Plugin.Fixture.Collision",
            ]);
            collision.ExitCode.Should().NotBe(0);
            collision.Output.Should().Contain("NANTO4108");

            ProcessResult malformed = await BuildConsumerAsync(root, feed, "malformed", ["Nanto.Plugin.Fixture.Malformed"]);
            malformed.ExitCode.Should().NotBe(0);
            malformed.Output.Should().Contain("NANTO4104");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task PackAsync(string repositoryRoot, string feed, string configuration, string project)
    {
        ProcessResult result = await RunAsync(
            repositoryRoot,
            "pack",
            Path.Combine(repositoryRoot, project.Replace('/', Path.DirectorySeparatorChar)),
            "--configuration",
            configuration,
            "--no-build",
            "--no-restore",
            "--output",
            feed);
        result.ExitCode.Should().Be(0, result.Output);
    }

    private static async Task<ProcessResult> BuildConsumerAsync(string root, string feed, string name, string[] plugins)
    {
        string projectDirectory = Path.Combine(root, name);
        string packages = Path.Combine(root, "packages", name);
        Directory.CreateDirectory(projectDirectory);
        string packageReferences = string.Join(
            Environment.NewLine,
            plugins.Select(static plugin => $"    <PackageReference Include=\"{plugin}\" Version=\"1.0.0\" />"));
        string projectPath = Path.Combine(projectDirectory, "Consumer.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                <RestorePackagesPath>{{SecurityElement.Escape(packages)}}</RestorePackagesPath>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Nanto.Sdk" Version="1.0.0" PrivateAssets="all" />
            {{packageReferences}}
              </ItemGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        string configPath = Path.Combine(projectDirectory, "NuGet.Config");
        await File.WriteAllTextAsync(
            configPath,
            $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="phase4-local" value="{{SecurityElement.Escape(feed)}}" />
              </packageSources>
            </configuration>
            """,
            TestContext.Current.CancellationToken);

        ProcessResult restore = await RunAsync(projectDirectory, "restore", projectPath, "--configfile", configPath, "--no-cache");
        restore.ExitCode.Should().Be(0, restore.Output);
        return await RunAsync(projectDirectory, "build", projectPath, "--no-restore");
    }

    private static async Task AssertChangedSelectionInputRequiresRestoreAsync(string root)
    {
        string projectDirectory = Path.Combine(root, "valid");
        string projectPath = Path.Combine(projectDirectory, "Consumer.csproj");
        string configPath = Path.Combine(projectDirectory, "NuGet.Config");
        string responsePath = Path.Combine(projectDirectory, "obj", "Debug", "net10.0", "Nanto", "Plugins", "plugin-inputs.rsp");
        string snapshotPath = Path.Combine(projectDirectory, "obj", "Debug", "net10.0", "Nanto", "Plugins", "nanto-plugin-selection-v1.json");
        string sdkPath = Path.Combine(root, "packages", "valid", "nanto.sdk", "1.0.0", "tools", "net10.0", "Nanto.Sdk.dll");
        await File.AppendAllTextAsync(projectPath, "<!-- freshness probe -->", TestContext.Current.CancellationToken);
        DateTime responseWriteTime = File.GetLastWriteTimeUtc(responsePath);
        DateTime snapshotWriteTime = File.GetLastWriteTimeUtc(snapshotPath);

        ProcessResult stale = await RunAsync(projectDirectory, sdkPath, "plugins", "verify", responsePath, snapshotPath);
        stale.ExitCode.Should().NotBe(0);
        stale.Output.Should().Contain("NANTO4115");
        File.GetLastWriteTimeUtc(responsePath).Should().Be(responseWriteTime);
        File.GetLastWriteTimeUtc(snapshotPath).Should().Be(snapshotWriteTime);

        ProcessResult restore = await RunAsync(projectDirectory, "restore", projectPath, "--configfile", configPath, "--no-cache");
        restore.ExitCode.Should().Be(0, restore.Output);
        ProcessResult refreshed = await RunAsync(projectDirectory, "build", projectPath, "--no-restore");
        refreshed.ExitCode.Should().Be(0, refreshed.Output);
        ProcessResult verified = await RunAsync(projectDirectory, sdkPath, "plugins", "verify", responsePath, snapshotPath);
        verified.ExitCode.Should().Be(0, verified.Output);

        string sdkTargetsPath = Path.Combine(root, "packages", "valid", "nanto.sdk", "1.0.0", "buildTransitive", "Nanto.Sdk.targets");
        await File.AppendAllTextAsync(sdkTargetsPath, "<!-- freshness probe -->", TestContext.Current.CancellationToken);
        ProcessResult staleSdkTargets = await RunAsync(projectDirectory, sdkPath, "plugins", "verify", responsePath, snapshotPath);
        staleSdkTargets.ExitCode.Should().NotBe(0);
        staleSdkTargets.Output.Should().Contain("NANTO4115");
    }

    private static async Task<ProcessResult> RunAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return new ProcessResult(process.ExitCode, await output + await error);
    }

    private static string GetAssemblyMetadata(string key) => typeof(PluginPackageConsumerTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == key)
        .Value!;

    private sealed record ProcessResult(int ExitCode, string Output);
}
