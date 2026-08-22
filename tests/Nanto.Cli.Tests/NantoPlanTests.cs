using AwesomeAssertions;

using Nanto.Cli.Configuration;
using Nanto.Cli.Planning;

namespace Nanto.Cli.Tests;

public sealed class NantoPlanTests
{
    [Fact]
    public void BuildOutputCannotReplaceTheApplicationRoot()
    {
        using var project = new TemporaryNantoProject();
        ValidatedNantoConfiguration configuration = NantoConfigurationLoader.Load(project.Root, null, null);

        var action = () => NantoPlanFactory.CreateBuildPlan(configuration, null, "Release", ".");

        action.Should().Throw<NantoConfigurationException>().WithMessage("*child directory*");
    }

    [Fact]
    public void DevelopmentPlanUsesStableLogicalPathsAndReverseShutdownOrder()
    {
        using var project = new TemporaryNantoProject();
        ValidatedNantoConfiguration configuration = NantoConfigurationLoader.Load(project.Root, null, null);

        NantoPlan plan = NantoPlanFactory.CreateDevelopmentPlan(configuration, null, hotReload: true);

        plan.Runtime.Should().Be("coreclr-framework-dependent");
        plan.Paths.Should().OnlyContain(path => !Path.IsPathRooted(path.RelativePath));
        plan.Steps.Select(step => step.Id).Should().Equal(
            "config.validate",
            "tools.validate",
            "frontend.restore",
            "contracts.generate",
            "frontend.start",
            "frontend.ready",
            "host.watch",
            "shutdown.host",
            "shutdown.frontend");
        plan.ShutdownOrder.Should().Equal("host", "frontend");
    }

    [Fact]
    public void BuildPlanIsDeterministicAcrossApplicationRoots()
    {
        using var first = new TemporaryNantoProject();
        using var second = new TemporaryNantoProject();
        NantoPlan firstPlan = NantoPlanFactory.CreateBuildPlan(NantoConfigurationLoader.Load(first.Root, null, null), null, "Release");
        NantoPlan secondPlan = NantoPlanFactory.CreateBuildPlan(NantoConfigurationLoader.Load(second.Root, null, null), null, "Release");

        NantoPlanRenderer.RenderJson(firstPlan).Should().Be(NantoPlanRenderer.RenderJson(secondPlan));
        firstPlan.Runtime.Should().Be("native-aot");
        firstPlan.RuntimeReason.Should().Contain("does not silently fall back");
    }

    [Fact]
    public void CreatingPlanDoesNotMutateApplicationDirectory()
    {
        using var project = new TemporaryNantoProject();
        string[] before = Directory.GetFileSystemEntries(project.Root, "*", SearchOption.AllDirectories);

        ValidatedNantoConfiguration configuration = NantoConfigurationLoader.Load(project.Root, null, null);
        _ = NantoPlanFactory.CreateDevelopmentPlan(configuration, null, hotReload: true);
        string[] after = Directory.GetFileSystemEntries(project.Root, "*", SearchOption.AllDirectories);

        after.Should().Equal(before);
    }

    [Fact]
    public void BuildPlanIncludesContainedOutputWithoutCreatingIt()
    {
        using var project = new TemporaryNantoProject();
        ValidatedNantoConfiguration configuration = NantoConfigurationLoader.Load(project.Root, null, null);

        NantoPlan plan = NantoPlanFactory.CreateBuildPlan(configuration, null, "Release", "out/product");

        plan.Paths.Should().ContainSingle(path => path.Label == "$output").Which.RelativePath.Should().Be("out/product");
        plan.Steps.Single(step => step.Id == "host.publish").Arguments.Should().ContainInOrder("--output", "$output");
        plan.Steps.Single(step => step.Id == "host.publish").Arguments.Should().Contain("-p:PublishAot=true");
        Directory.Exists(Path.Combine(project.Root, "out")).Should().BeFalse();
    }

    [Fact]
    public void BuildPlanRejectsEscapingOutput()
    {
        using var project = new TemporaryNantoProject();
        ValidatedNantoConfiguration configuration = NantoConfigurationLoader.Load(project.Root, null, null);

        var action = () => NantoPlanFactory.CreateBuildPlan(configuration, null, "Release", "../outside");

        action.Should().Throw<NantoConfigurationException>().WithMessage("*inside the application root*");
    }
}
