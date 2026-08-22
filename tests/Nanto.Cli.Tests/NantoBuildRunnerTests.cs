using AwesomeAssertions;

using Nanto.Cli.Execution;

namespace Nanto.Cli.Tests;

public sealed class NantoBuildRunnerTests
{
    [Fact]
    public void NativeAotInspectionRejectsCoreClrDeploymentShape()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "App.exe"), "host");
        File.WriteAllText(Path.Combine(directory.Path, "coreclr.dll"), "runtime");

        var action = () => NantoBuildRunner.InspectArtifacts(directory.Path, "native-aot");

        action.Should().Throw<NantoExecutionException>().Which.Code.Should().Be("NANTO_NATIVE_AOT_SHAPE_INVALID");
    }

    [Fact]
    public void NativeAotInspectionAcceptsSingleExecutable()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "App.exe"), "native");

        var action = () => NantoBuildRunner.InspectArtifacts(directory.Path, "native-aot");

        action.Should().NotThrow();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nanto-build-runner-{Guid.NewGuid():N}");

        internal TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
