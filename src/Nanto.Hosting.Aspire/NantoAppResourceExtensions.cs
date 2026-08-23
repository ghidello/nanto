using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Provides optional Aspire resource-model integration for Nanto development applications.</summary>
public static class NantoAppResourceExtensions
{
    /// <summary>Adds a Nanto host watched by the .NET SDK and connected to an Aspire-managed frontend endpoint.</summary>
    public static IResourceBuilder<ExecutableResource> AddNantoApp<TFrontend>(
        this IDistributedApplicationBuilder builder,
        string name,
        string projectPath,
        IResourceBuilder<TFrontend> frontend,
        string generatedClientPath,
        string frontendEndpointName = "http",
        string configuration = "Debug")
        where TFrontend : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(frontend);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedClientPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(frontendEndpointName);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        string fullProjectPath = Path.GetFullPath(projectPath);
        string projectDirectory = Path.GetDirectoryName(fullProjectPath)!;
        if (!File.Exists(fullProjectPath))
        {
            throw new FileNotFoundException("The Nanto host project was not found.", fullProjectPath);
        }

        return builder.AddExecutable(
                name,
                "dotnet",
                projectDirectory,
                "watch",
                "--project",
                Path.GetFileName(fullProjectPath),
                "--configuration",
                configuration)
            .WithEnvironment("NANTO_DEVELOPMENT_URL", frontend.GetEndpoint(frontendEndpointName))
            .WithEnvironment("NantoFrontendOutputPath", Path.GetFullPath(generatedClientPath))
            .WithEnvironment("DOTNET_WATCH_RESTART_ON_RUDE_EDIT", "1")
            .WithOtlpExporter()
            .WaitFor(builder.CreateResourceBuilder<IResource>(frontend.Resource));
    }
}
