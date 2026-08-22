using Nanto.Cli.Configuration;

namespace Nanto.Cli.Planning;

internal static class NantoPlanFactory
{
    internal static NantoPlan CreateDevelopmentPlan(ValidatedNantoConfiguration configuration, string? runtimeOverride, bool hotReload)
    {
        (string runtime, string reason) = ResolveRuntime(runtimeOverride ?? "coreclr-framework-dependent", development: true);
        var steps = new List<NantoPlanStep>
        {
            Step("config.validate", [], "validation", "$application", mutation: "none"),
            Step("tools.validate", ["config.validate"], "validation", "$application", mutation: "none"),
        };
        string previousStep = "tools.validate";
        if (configuration.Value.Frontend.Install is { } install)
        {
            steps.Add(CommandStep("frontend.restore", [previousStep], "$frontend", install, "filesystem"));
            previousStep = "frontend.restore";
        }

        steps.Add(Step(
            "contracts.generate",
            [previousStep],
            "process",
            "$application",
            "dotnet",
            ["build", "$hostProject", "--configuration", "Debug", "--nologo"],
            "filesystem"));
        string readinessDependency;
        if (configuration.Value.Frontend.Dev is { File: { } devFile, Arguments: { } devArguments })
        {
            steps.Add(CommandStep(
                "frontend.start",
                ["contracts.generate"],
                "$frontend",
                new NantoCommandConfiguration { File = devFile, Arguments = devArguments },
                "process-tree"));
            readinessDependency = "frontend.start";
        }
        else
        {
            readinessDependency = "contracts.generate";
        }

        steps.Add(Step(
            "frontend.ready",
            [readinessDependency],
            "readiness",
            "$frontend",
            mutation: "none",
            timeoutSeconds: configuration.Value.Frontend.Dev.ReadyTimeoutSeconds,
            readinessUrl: configuration.DevelopmentUri.AbsoluteUri));
        steps.Add(Step(
            "host.watch",
            ["frontend.ready"],
            "process",
            "$application",
            "dotnet",
            hotReload
                ? ["watch", "--project", "$hostProject"]
                : ["watch", "--project", "$hostProject", "--no-hot-reload"],
            "process-tree"));
        steps.Add(Step("shutdown.host", ["host.watch"], "shutdown", "$application", mutation: "process-tree"));
        if (configuration.Value.Frontend.Dev.IsManaged)
        {
            steps.Add(Step("shutdown.frontend", ["shutdown.host"], "shutdown", "$frontend", mutation: "process-tree"));
        }

        return Create(
            "dev",
            runtime,
            reason,
            configuration,
            [.. steps],
            configuration.Value.Frontend.Dev.IsManaged ? ["host", "frontend"] : ["host"]);
    }

    internal static NantoPlan CreateBuildPlan(
        ValidatedNantoConfiguration configuration,
        string? runtimeOverride,
        string buildConfiguration,
        string? output = null)
    {
        (string runtime, string reason) = ResolveRuntime(runtimeOverride ?? configuration.Value.Build.Runtime, development: false);
        string outputPath = ResolveOutputPath(configuration.ApplicationRoot, output);
        var steps = new List<NantoPlanStep>
        {
            Step("config.validate", [], "validation", "$application", mutation: "none"),
            Step("tools.validate", ["config.validate"], "validation", "$application", mutation: "none"),
        };
        string previousStep = "tools.validate";
        if (configuration.Value.Frontend.Install is { } install)
        {
            steps.Add(CommandStep("frontend.restore", [previousStep], "$frontend", install, "filesystem"));
            previousStep = "frontend.restore";
        }

        steps.Add(Step(
            "contracts.generate",
            [previousStep],
            "process",
            "$application",
            "dotnet",
            ["build", "$hostProject", "--configuration", buildConfiguration, "--nologo"],
            "filesystem"));
        steps.Add(CommandStep("frontend.build", ["contracts.generate"], "$frontend", configuration.Value.Frontend.Build, "filesystem"));
        steps.Add(Step("assets.validate", ["frontend.build"], "validation", "$frontendDist", mutation: "none"));
        steps.Add(Step(
            "host.publish",
            ["assets.validate"],
            "process",
            "$application",
            "dotnet",
            [
                "publish",
                "$hostProject",
                "--configuration",
                buildConfiguration,
                "--output",
                "$output",
                "-p:NantoBuildMode=" + ToBuildMode(runtime),
                .. ToPublishProperties(runtime),
            ],
            "filesystem"));
        steps.Add(Step("artifacts.inspect", ["host.publish"], "inspection", "$application", mutation: "none"));

        NantoPlan plan = Create("build", runtime, reason, configuration, [.. steps], []);
        return plan with
        {
            Paths = [.. plan.Paths, new NantoPlanPath { Label = "$output", RelativePath = ToRelative(configuration.ApplicationRoot, outputPath) }],
        };
    }

    private static NantoPlan Create(
        string command,
        string runtime,
        string reason,
        ValidatedNantoConfiguration configuration,
        NantoPlanStep[] steps,
        string[] shutdownOrder) => new()
        {
            Command = command,
            Runtime = runtime,
            RuntimeReason = reason,
            Paths =
            [
                new() { Label = "$application", RelativePath = "." },
                new() { Label = "$hostProject", RelativePath = ToRelative(configuration.ApplicationRoot, configuration.HostProjectPath) },
                new() { Label = "$frontend", RelativePath = ToRelative(configuration.ApplicationRoot, configuration.FrontendDirectory) },
                new() { Label = "$generatedClient", RelativePath = ToRelative(configuration.ApplicationRoot, configuration.GeneratedClientDirectory) },
                new() { Label = "$frontendDist", RelativePath = ToRelative(configuration.ApplicationRoot, configuration.FrontendDistDirectory) },
            ],
            Steps = steps,
            ShutdownOrder = shutdownOrder,
        };

    private static NantoPlanStep CommandStep(
        string id,
        string[] dependencies,
        string workingDirectory,
        NantoCommandConfiguration command,
        string mutation) => Step(id, dependencies, "process", workingDirectory, command.File, command.Arguments, mutation);

    private static NantoPlanStep Step(
        string id,
        string[] dependencies,
        string kind,
        string workingDirectory,
        string? file = null,
        string[]? arguments = null,
        string mutation = "none",
        int? timeoutSeconds = null,
        string? readinessUrl = null) => new()
        {
            Id = id,
            DependsOn = dependencies,
            Kind = kind,
            WorkingDirectory = workingDirectory,
            File = file,
            Arguments = arguments ?? [],
            Mutation = mutation,
            TimeoutSeconds = timeoutSeconds,
            ReadinessUrl = readinessUrl,
        };

    private static (string Runtime, string Reason) ResolveRuntime(string value, bool development)
    {
        if (development && value is "auto" or "coreclr" or "coreclr-framework-dependent")
        {
            return ("coreclr-framework-dependent", "Development uses framework-dependent CoreCLR for managed watch and Hot Reload.");
        }

        return value switch
        {
            "auto" => ("native-aot", "Auto selects Native AOT and does not silently fall back."),
            "native-aot" => ("native-aot", "Native AOT was selected explicitly."),
            "coreclr" => ("coreclr", "Self-contained CoreCLR was selected explicitly."),
            "coreclr-framework-dependent" => ("coreclr-framework-dependent", "Framework-dependent CoreCLR was selected explicitly."),
            _ => throw new NantoConfigurationException("Unsupported runtime override."),
        };
    }

    private static string ToBuildMode(string runtime) => runtime switch
    {
        "native-aot" => "NativeAot",
        "coreclr" => "CoreClrSelfContained",
        "coreclr-framework-dependent" => "CoreClrFrameworkDependent",
        _ => throw new InvalidOperationException("The plan contains an unsupported runtime."),
    };

    private static string[] ToPublishProperties(string runtime) => runtime switch
    {
        "native-aot" =>
        [
            "-p:PublishAot=true",
            "-p:PublishTrimmed=true",
            "-p:SelfContained=true",
            "-p:TrimMode=full",
            "-p:TrimmerSingleWarn=false",
            "-p:UseAppHost=true",
        ],
        "coreclr" =>
        [
            "-p:PublishAot=false",
            "-p:PublishTrimmed=false",
            "-p:SelfContained=true",
            "-p:UseAppHost=true",
        ],
        "coreclr-framework-dependent" =>
        [
            "-p:PublishAot=false",
            "-p:PublishTrimmed=false",
            "-p:SelfContained=false",
            "-p:UseAppHost=false",
        ],
        _ => throw new InvalidOperationException("The plan contains an unsupported runtime."),
    };

    private static string ToRelative(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return string.IsNullOrEmpty(relative) ? "." : relative;
    }

    internal static string ResolveOutputPath(string applicationRoot, string? output)
    {
        if (output?.Contains('\0') == true)
        {
            throw new NantoConfigurationException("Output path is invalid.");
        }

        string path;
        try
        {
            path = Path.GetFullPath(output ?? Path.Combine("artifacts", "publish"), applicationRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new NantoConfigurationException("Output path is invalid.");
        }

        string relative = Path.GetRelativePath(applicationRoot, path);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new NantoConfigurationException("Output path must be a child directory inside the application root.");
        }

        return path;
    }
}
