var builder = DistributedApplication.CreateBuilder(args);

var dependency = builder.AddProject("dependency", "./DependencyService/Nanto.Aspire.Sample.DependencyService.csproj")
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health");
var frontend = builder.AddViteApp("frontend", "./Frontend")
    .WithNpm();

builder.AddNantoApp(
        "nanto-app",
        "./App/Nanto.Aspire.Sample.App.csproj",
        frontend,
        "./Frontend/src/generated/nanto/app")
    .WithEnvironment("DEPENDENCY_URL", dependency.GetEndpoint("http"))
    .WaitFor(dependency);

builder.Build().Run();
