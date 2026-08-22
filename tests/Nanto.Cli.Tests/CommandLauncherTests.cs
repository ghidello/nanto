using AwesomeAssertions;

using Nanto.Cli.Processes;

namespace Nanto.Cli.Tests;

public sealed class CommandLauncherTests
{
    [Fact]
    public void BuildWindowsShimCommandPreservesSpacesAndUnicodeAsArgumentBoundaries()
    {
        string command = CommandLauncher.BuildWindowsShimCommand(
            @"C:\Program Files\nodejs\npm.cmd",
            ["run", "dev", "perché insieme"]);

        command.Should().Be("\"C:\\Program Files\\nodejs\\npm.cmd\" \"run\" \"dev\" \"perché insieme\"");
    }

    [Theory]
    [InlineData("value%PATH%")]
    [InlineData("value\"quote")]
    [InlineData("line\nbreak")]
    public void BuildWindowsShimCommandRejectsExpansionAndAmbiguousQuoting(string argument)
    {
        var action = () => CommandLauncher.BuildWindowsShimCommand("npm.cmd", [argument]);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateUsesExplicitCommandInterpreterAdapterForBatchShim()
    {
        using var project = new TemporaryNantoProject();
        string shim = project.Write("tools/test.cmd", "@exit /b 0");
        var command = new ProcessCommand
        {
            File = shim,
            Arguments = ["one", "two words"],
            WorkingDirectory = project.Root,
        };

        System.Diagnostics.ProcessStartInfo startInfo = CommandLauncher.Create(command);

        Path.GetFileName(startInfo.FileName).Should().BeEquivalentTo("cmd.exe");
        startInfo.ArgumentList.Should().BeEmpty();
        startInfo.Arguments.Should().Be($"/d /s /c \"\"{Path.GetFullPath(shim)}\" \"one\" \"two words\"\"");
        startInfo.UseShellExecute.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessRunnerExecutesBatchShimWithQuotedPathAndArgument()
    {
        using var project = new TemporaryNantoProject();
        string shim = project.Write("tools with spaces/test.cmd", "@echo %~1\r\n@exit /b 0");
        var command = new ProcessCommand
        {
            File = shim,
            Arguments = ["two words"],
            WorkingDirectory = project.Root,
        };
        var runner = new ProcessRunner(TextWriter.Null);

        ProcessRunResult result = await runner.RunAsync(command, "test", CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.DiagnosticLines.Should().Contain("two words");
    }
}
