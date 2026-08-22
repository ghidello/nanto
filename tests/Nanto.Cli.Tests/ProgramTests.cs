using AwesomeAssertions;

namespace Nanto.Cli.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void RunEmitsJsonPlanWithoutExecutingCommands()
    {
        using var project = new TemporaryNantoProject();
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = Program.Run(["build", "--plan", "--format", "json"], project.Root, output, error);

        exitCode.Should().Be(Program.Success);
        output.ToString().Should().Contain("\"command\": \"build\"");
        output.ToString().Should().Contain("\"host.publish\"");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public void DoctorHumanOutputDoesNotExposeAbsolutePaths()
    {
        using var project = new TemporaryNantoProject();
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = Program.Run(["doctor"], project.Root, output, error);

        exitCode.Should().Be(Program.Success);
        output.ToString().Should().NotContain(project.Root);
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public void RunRejectsCommandSpecificOptions()
    {
        using var project = new TemporaryNantoProject();
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = Program.Run(["doctor", "--runtime", "native-aot"], project.Root, output, error);

        exitCode.Should().Be(Program.UsageError);
        error.ToString().Should().Contain("not valid for doctor");
    }
}
