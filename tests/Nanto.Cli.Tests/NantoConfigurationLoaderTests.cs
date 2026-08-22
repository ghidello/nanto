using AwesomeAssertions;

using Nanto.Cli.Configuration;

namespace Nanto.Cli.Tests;

public sealed class NantoConfigurationLoaderTests
{
    [Fact]
    public void LoadValidatesAndNormalizesConfiguration()
    {
        using var project = new TemporaryNantoProject();

        ValidatedNantoConfiguration configuration = NantoConfigurationLoader.Load(project.Root, null, null);

        configuration.ApplicationRoot.Should().Be(project.Root);
        configuration.HostProjectPath.Should().Be(Path.Combine(project.Root, "MyApp.csproj"));
        configuration.FrontendDirectory.Should().Be(Path.Combine(project.Root, "Frontend"));
        configuration.GeneratedClientDirectory.Should().Be(Path.Combine(project.Root, "Frontend", "src", "generated", "nanto"));
        configuration.FrontendDistDirectory.Should().Be(Path.Combine(project.Root, "Frontend", "dist"));
        configuration.FrontendEntryAssetPath.Should().Be(Path.Combine(project.Root, "Frontend", "dist", "index.html"));
        configuration.DevelopmentUri.Should().Be(new Uri("http://127.0.0.1:5173"));
    }

    [Fact]
    public void LoadRejectsDuplicateProperties()
    {
        using var project = new TemporaryNantoProject(TemporaryNantoProject.ValidConfiguration.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"schemaVersion\": 1,",
            StringComparison.Ordinal));

        var action = () => NantoConfigurationLoader.Load(project.Root, null, null);

        action.Should().Throw<NantoConfigurationException>().WithMessage("*duplicate property 'schemaVersion'*");
    }

    [Fact]
    public void LoadRejectsUnknownProperties()
    {
        using var project = new TemporaryNantoProject(TemporaryNantoProject.ValidConfiguration.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"unexpected\": true,",
            StringComparison.Ordinal));

        var action = () => NantoConfigurationLoader.Load(project.Root, null, null);

        action.Should().Throw<NantoConfigurationException>().WithMessage("*unexpected*");
    }

    [Fact]
    public void LoadMergesEnvironmentObjectsAndReplacesArrays()
    {
        using var project = new TemporaryNantoProject();
        project.Write(
            "nanto.Development.json",
            """
            {
              "frontend": {
                "dev": {
                  "arguments": ["run", "serve"],
                  "readyTimeoutSeconds": 15
                }
              }
            }
            """);

        ValidatedNantoConfiguration configuration = NantoConfigurationLoader.Load(project.Root, null, "Development");

        configuration.Value.Frontend.Dev.Arguments.Should().Equal("run", "serve");
        configuration.Value.Frontend.Dev.ReadyTimeoutSeconds.Should().Be(15);
        configuration.Value.Frontend.Dev.File.Should().Be("npm");
    }

    [Theory]
    [InlineData("../outside.csproj", "Frontend", "src/generated/nanto", "dist")]
    [InlineData("MyApp.csproj", "../Frontend", "src/generated/nanto", "dist")]
    [InlineData("MyApp.csproj", "Frontend", "../generated", "dist")]
    [InlineData("MyApp.csproj", "Frontend", "src/generated/nanto", "../dist")]
    public void LoadRejectsEscapingPaths(string hostProject, string frontend, string generatedClient, string dist)
    {
        string configuration = TemporaryNantoProject.ValidConfiguration
            .Replace("MyApp.csproj", hostProject, StringComparison.Ordinal)
            .Replace("\"Frontend\"", $"\"{frontend}\"", StringComparison.Ordinal)
            .Replace("src/generated/nanto", generatedClient, StringComparison.Ordinal)
            .Replace("\"dist\": \"dist\"", $"\"dist\": \"{dist}\"", StringComparison.Ordinal);
        using var project = new TemporaryNantoProject(configuration);

        var action = () => NantoConfigurationLoader.Load(project.Root, null, null);

        action.Should().Throw<NantoConfigurationException>().WithMessage("*must remain inside*");
    }

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("ftp://127.0.0.1:5173")]
    [InlineData("http://user@127.0.0.1:5173")]
    [InlineData("/relative")]
    public void LoadRejectsInvalidDevelopmentUrls(string url)
    {
        using var project = new TemporaryNantoProject(TemporaryNantoProject.ValidConfiguration.Replace(
            "http://127.0.0.1:5173",
            url,
            StringComparison.Ordinal));

        var action = () => NantoConfigurationLoader.Load(project.Root, null, null);

        action.Should().Throw<NantoConfigurationException>().WithMessage("*explicit port*");
    }

    [Fact]
    public void LoadRejectsInvalidEnvironmentNameBeforeReadingAnOverlay()
    {
        using var project = new TemporaryNantoProject();

        var action = () => NantoConfigurationLoader.Load(project.Root, null, "../Production");

        action.Should().Throw<NantoConfigurationException>().WithMessage("*single valid file-name segment*");
    }
}
