using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Nanto.Hosting.Aspire.Tests;

public sealed class NantoAppResourceExtensionsTests
{
    [Fact]
    public void AddNantoAppBuildsInspectableExecutableGraph()
    {
        string projectDirectory = Path.Combine(Path.GetTempPath(), "nanto-aspire-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDirectory);
        string projectPath = Path.Combine(projectDirectory, "Sample.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        try
        {
            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
            IResourceBuilder<ExecutableResource> frontend = builder.AddExecutable("frontend", "frontend.exe", projectDirectory)
                .WithHttpEndpoint(name: "http");

            IResourceBuilder<ExecutableResource> app = builder.AddNantoApp("app", projectPath, frontend, Path.Combine(projectDirectory, "generated"));

            Assert.Equal("dotnet", app.Resource.Command);
            Assert.Equal(projectDirectory, app.Resource.WorkingDirectory);
            WaitAnnotation wait = Assert.Single(app.Resource.Annotations.OfType<WaitAnnotation>());
            Assert.Same(frontend.Resource, wait.Resource);
            Assert.Equal(4, app.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());
            Assert.Contains(builder.Resources, resource => ReferenceEquals(resource, app.Resource));
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Fact]
    public void AddNantoAppRejectsMissingProjectBeforeMutatingModel()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder([]);
        IResourceBuilder<ExecutableResource> frontend = builder.AddExecutable("frontend", "frontend.exe", Path.GetTempPath())
            .WithHttpEndpoint(name: "http");
        int initialResources = builder.Resources.Count;

        var action = () => builder.AddNantoApp(
            "app",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.csproj"),
            frontend,
            Path.Combine(Path.GetTempPath(), "generated"));

        Assert.Throws<FileNotFoundException>(action);
        Assert.Equal(initialResources, builder.Resources.Count);
    }
}
