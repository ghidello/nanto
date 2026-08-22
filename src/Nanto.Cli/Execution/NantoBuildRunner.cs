using Nanto.Cli.Configuration;
using Nanto.Cli.Planning;
using Nanto.Cli.Processes;

namespace Nanto.Cli.Execution;

internal sealed class NantoBuildRunner
{
    private readonly ProcessRunner _processRunner;
    private readonly TextWriter _output;

    internal NantoBuildRunner(TextWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _processRunner = new ProcessRunner(output);
    }

    internal async Task<int> RunAsync(
        ValidatedNantoConfiguration configuration,
        string buildConfiguration,
        string? runtimeOverride,
        string? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Value.Frontend.Install is { } install)
        {
            await RunRequiredStepAsync(
                ToCommand(install, configuration.FrontendDirectory),
                "frontend",
                "frontend.restore",
                5,
                cancellationToken);
        }

        await RunRequiredStepAsync(
            CreateContractBuild(configuration, buildConfiguration),
            "build",
            "contracts.generate",
            6,
            cancellationToken);
        await RunRequiredStepAsync(
            ToCommand(configuration.Value.Frontend.Build, configuration.FrontendDirectory),
            "frontend",
            "frontend.build",
            5,
            cancellationToken);
        ValidateAssets(configuration);

        string runtime = ResolveRuntime(runtimeOverride ?? configuration.Value.Build.Runtime);
        string outputPath = NantoPlanFactory.ResolveOutputPath(configuration.ApplicationRoot, output);
        string stagingPath = CreateStagingPath(outputPath);
        try
        {
            ProcessRunResult publish = await _processRunner.RunAsync(
                CreatePublish(configuration, buildConfiguration, runtime, stagingPath),
                "build",
                cancellationToken);
            if (publish.ExitCode != 0 || publish.ForcedTermination)
            {
                throw new NantoExecutionException("NANTO_PUBLISH_FAILED", "host.publish", "The host publish failed.", publish.ExitCode);
            }

            InspectArtifacts(stagingPath, runtime);
            PromoteArtifacts(stagingPath, outputPath);
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }

        long installedBytes = Directory.EnumerateFiles(outputPath, "*", SearchOption.AllDirectories).Sum(static path => new FileInfo(path).Length);
        await _output.WriteLineAsync($"[build] runtime={runtime}; output=$output; installed-bytes={installedBytes}");
        return 0;
    }

    private async Task RunRequiredStepAsync(
        ProcessCommand command,
        string resource,
        string stepId,
        int category,
        CancellationToken cancellationToken)
    {
        ProcessRunResult result = await _processRunner.RunAsync(command, resource, cancellationToken);
        if (result.ExitCode != 0 || result.ForcedTermination)
        {
            throw new NantoExecutionException(
                category == 5 ? "NANTO_FRONTEND_FAILED" : "NANTO_BUILD_FAILED",
                stepId,
                "A required build step failed.",
                result.ExitCode);
        }
    }

    private static void ValidateAssets(ValidatedNantoConfiguration configuration)
    {
        if (!Directory.Exists(configuration.FrontendDistDirectory) || !File.Exists(configuration.FrontendEntryAssetPath))
        {
            throw new NantoExecutionException(
                "NANTO_ASSETS_INVALID",
                "assets.validate",
                "The frontend distribution or its declared entry asset does not exist.");
        }
    }

    internal static void InspectArtifacts(string outputPath, string runtime)
    {
        if (!Directory.Exists(outputPath))
        {
            throw new NantoExecutionException("NANTO_ARTIFACTS_MISSING", "artifacts.inspect", "The publish output directory does not exist.");
        }

        if (Directory.EnumerateFiles(outputPath, "*.pdb", SearchOption.AllDirectories).Any())
        {
            throw new NantoExecutionException("NANTO_PDB_IN_PRODUCT", "artifacts.inspect", "The publish output contains PDB files.");
        }

        if (runtime == "native-aot")
        {
            string[] files = Directory.EnumerateFiles(outputPath, "*", SearchOption.AllDirectories).ToArray();
            if (files.Length != 1 || !string.Equals(Path.GetExtension(files[0]), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new NantoExecutionException(
                    "NANTO_NATIVE_AOT_SHAPE_INVALID",
                    "artifacts.inspect",
                    "The Native AOT publish must contain exactly one executable and no CoreCLR deployment files.");
            }
        }
    }

    private static string CreateStagingPath(string outputPath)
    {
        string parent = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, $".{Path.GetFileName(outputPath)}.nanto-staging-{Guid.NewGuid():N}");
    }

    private static void PromoteArtifacts(string stagingPath, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            throw new NantoExecutionException("NANTO_OUTPUT_INVALID", "artifacts.inspect", "The build output path is an existing file.");
        }

        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }

        Directory.Move(stagingPath, outputPath);
    }

    private static ProcessCommand CreateContractBuild(ValidatedNantoConfiguration configuration, string buildConfiguration) => new()
    {
        File = "dotnet",
        Arguments =
        [
            "build",
            configuration.HostProjectPath,
            "--configuration",
            buildConfiguration,
            "--nologo",
            $"-p:NantoFrontendOutputPath={configuration.GeneratedClientDirectory}",
        ],
        WorkingDirectory = configuration.ApplicationRoot,
    };

    internal static ProcessCommand CreatePublish(
        ValidatedNantoConfiguration configuration,
        string buildConfiguration,
        string runtime,
        string outputPath) => new()
        {
            File = "dotnet",
            Arguments =
            [
                "publish",
                configuration.HostProjectPath,
                "--configuration",
                buildConfiguration,
                "--output",
                outputPath,
                "--nologo",
                $"-p:NantoBuildMode={ToBuildMode(runtime)}",
                $"-p:NantoFrontendOutputPath={configuration.GeneratedClientDirectory}",
                $"-p:NantoFrontendDist={configuration.FrontendDistDirectory}",
                "-p:NantoIncludeProductionAssets=true",
                "-p:NantoRequireProductionAssets=true",
                .. ToPublishProperties(runtime),
            ],
            WorkingDirectory = configuration.ApplicationRoot,
        };

    private static ProcessCommand ToCommand(NantoCommandConfiguration command, string workingDirectory) => new()
    {
        File = command.File,
        Arguments = command.Arguments,
        WorkingDirectory = workingDirectory,
    };

    private static string ResolveRuntime(string runtime) => runtime switch
    {
        "auto" => "native-aot",
        "native-aot" or "coreclr" or "coreclr-framework-dependent" => runtime,
        _ => throw new NantoExecutionException("NANTO_RUNTIME_INVALID", "config.validate", "The runtime profile is unsupported."),
    };

    private static string ToBuildMode(string runtime) => runtime switch
    {
        "native-aot" => "NativeAot",
        "coreclr" => "CoreClrSelfContained",
        "coreclr-framework-dependent" => "CoreClrFrameworkDependent",
        _ => throw new InvalidOperationException("The runtime profile is unsupported."),
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
        _ => throw new InvalidOperationException("The runtime profile is unsupported."),
    };
}
