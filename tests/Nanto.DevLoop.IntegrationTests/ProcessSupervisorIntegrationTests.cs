using System.Diagnostics;

using AwesomeAssertions;

using Nanto.Cli.Processes;
using Nanto.Cli.ProcessFixture;

namespace Nanto.DevLoop.IntegrationTests;

public sealed class ProcessSupervisorIntegrationTests
{
    [Fact]
    public async Task ForcedStopTerminatesImmediateGrandchildAndReleasesLockedFile()
    {
        string root = CreateTemporaryDirectory();
        string processIdPath = Path.Combine(root, "child.pid");
        string lockPath = Path.Combine(root, "held.lock");
        try
        {
            using var output = new StringWriter();
            await using OwnedProcess process = OwnedProcess.Start(
                FixtureCommand("spawn-grandchild", processIdPath, lockPath),
                "fixture",
                output);
            await WaitForFileAsync(processIdPath, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            int childId = int.Parse(
                await File.ReadAllTextAsync(processIdPath, TestContext.Current.CancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            await WaitForFileAsync(lockPath, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            ProcessRunResult result = await process.StopAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

            result.ForcedTermination.Should().BeTrue();
            IsRunning(process.Id).Should().BeFalse();
            IsRunning(childId).Should().BeFalse();
            using FileStream unlocked = new(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OutputFloodIsDrainedAndRetainedDiagnosticsStayBounded()
    {
        using var output = new StringWriter();
        await using OwnedProcess process = OwnedProcess.Start(FixtureCommand("flood"), "fixture", output);

        ProcessRunResult result = await process.WaitAsync(TestContext.Current.CancellationToken);

        result.ExitCode.Should().Be(0);
        result.DiagnosticLines.Should().HaveCount(32);
        result.DiagnosticLines[^1].Should().EndWith("…[truncated]");
    }

    [Fact]
    public async Task ImmediateExitIsObservedReliablyWithoutReopeningTheProcessById()
    {
        for (var index = 0; index < 20; index++)
        {
            await using OwnedProcess process = OwnedProcess.Start(FixtureCommand("exit", "0"), "fixture", TextWriter.Null);

            ProcessRunResult result = await process.WaitAsync(TestContext.Current.CancellationToken);

            result.ExitCode.Should().Be(0);
            process.HasExited.Should().BeTrue();
        }
    }

    private static ProcessCommand FixtureCommand(params string[] arguments) => new()
    {
        File = "dotnet",
        Arguments = [typeof(ProcessFixtureMarker).Assembly.Location, .. arguments],
        WorkingDirectory = Path.GetDirectoryName(typeof(ProcessFixtureMarker).Assembly.Location)!,
    };

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "nanto-devloop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        while (!File.Exists(path))
        {
            await Task.Delay(25, cancellation.Token);
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
