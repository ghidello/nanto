using Nanto.Cli.Configuration;
using Nanto.Cli.Processes;

namespace Nanto.Cli.Execution;

internal sealed class NantoDevelopmentRunner
{
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly ProcessRunner _processRunner;
    private readonly ReadinessProbe _readiness;
    private readonly TextWriter _output;

    internal NantoDevelopmentRunner(TextWriter output, ReadinessProbe? readiness = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _processRunner = new ProcessRunner(output, _shutdownTimeout);
        _readiness = readiness ?? new ReadinessProbe();
    }

    internal async Task<int> RunAsync(
        ValidatedNantoConfiguration configuration,
        string buildConfiguration,
        bool hotReload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        OwnedProcess? frontend = null;
        OwnedProcess? host = null;
        try
        {
            if (configuration.Value.Frontend.Install is { } install)
            {
                await RunRequiredStepAsync(ToCommand(install, configuration.FrontendDirectory), "frontend", "frontend.restore", cancellationToken);
            }

            await RunRequiredStepAsync(CreateContractBuild(configuration, buildConfiguration), "build", "contracts.generate", cancellationToken);

            NantoDevelopmentConfiguration development = configuration.Value.Frontend.Dev;
            if (development.IsManaged)
            {
                if (await _readiness.IsReachableAsync(configuration.DevelopmentUri, cancellationToken))
                {
                    throw new NantoExecutionException("NANTO_FRONTEND_PORT_OCCUPIED", "frontend.start", "The managed frontend URL is already reachable.");
                }

                frontend = _processRunner.Start(
                    new ProcessCommand
                    {
                        File = development.File!,
                        Arguments = development.Arguments!,
                        WorkingDirectory = configuration.FrontendDirectory,
                    },
                    "frontend");
                await _readiness.WaitAsync(
                    configuration.DevelopmentUri,
                    TimeSpan.FromSeconds(development.ReadyTimeoutSeconds),
                    () => frontend.HasExited,
                    cancellationToken);
            }
            else if (!await _readiness.IsReachableAsync(configuration.DevelopmentUri, cancellationToken))
            {
                throw new NantoExecutionException(
                    "NANTO_EXTERNAL_FRONTEND_UNREACHABLE",
                    "frontend.ready",
                    "The externally managed frontend URL is not reachable.");
            }

            await _output.WriteLineAsync("[frontend] ready");
            host = _processRunner.Start(CreateWatchCommand(configuration, buildConfiguration, hotReload), "host");
            await _output.WriteLineAsync("[host] started");

            Task<ProcessRunResult> hostExit = host.WaitAsync(cancellationToken);
            if (frontend is null)
            {
                ProcessRunResult hostResult = await hostExit;
                return hostResult.ExitCode;
            }

            Task<ProcessRunResult> frontendExit = frontend.WaitAsync(cancellationToken);
            Task completed = await Task.WhenAny(hostExit, frontendExit);
            if (completed == frontendExit)
            {
                ProcessRunResult frontendResult = await frontendExit;
                await host.StopAsync(_shutdownTimeout, CancellationToken.None);
                return 5;
            }

            ProcessRunResult result = await hostExit;
            await frontend.StopAsync(_shutdownTimeout, CancellationToken.None);
            return result.ExitCode == 0 ? 0 : 6;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await StopAsync(host, frontend);
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }

            if (frontend is not null)
            {
                await frontend.DisposeAsync();
            }
        }
    }

    private async Task RunRequiredStepAsync(ProcessCommand command, string resource, string stepId, CancellationToken cancellationToken)
    {
        ProcessRunResult result = await _processRunner.RunAsync(command, resource, cancellationToken);
        if (result.ExitCode != 0 || result.ForcedTermination)
        {
            throw new NantoExecutionException("NANTO_PROCESS_FAILED", stepId, "A required process step failed.", result.ExitCode);
        }
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

    private static ProcessCommand CreateWatchCommand(
        ValidatedNantoConfiguration configuration,
        string buildConfiguration,
        bool hotReload) => new()
        {
            File = "dotnet",
            Arguments = hotReload
                ? ["watch", "--project", configuration.HostProjectPath, "--configuration", buildConfiguration]
                : ["watch", "--project", configuration.HostProjectPath, "--configuration", buildConfiguration, "--no-hot-reload"],
            WorkingDirectory = configuration.ApplicationRoot,
            Environment = new Dictionary<string, string?>
            {
                ["NANTO_DEVELOPMENT_URL"] = configuration.DevelopmentUri.AbsoluteUri,
                ["NantoFrontendOutputPath"] = configuration.GeneratedClientDirectory,
                ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "1",
            },
        };

    private static ProcessCommand ToCommand(NantoCommandConfiguration command, string workingDirectory) => new()
    {
        File = command.File,
        Arguments = command.Arguments,
        WorkingDirectory = workingDirectory,
    };

    private static async Task<int> StopAsync(OwnedProcess? host, OwnedProcess? frontend)
    {
        ProcessRunResult? hostResult = host is null ? null : await host.StopAsync(_shutdownTimeout);
        ProcessRunResult? frontendResult = frontend is null ? null : await frontend.StopAsync(_shutdownTimeout);
        return hostResult?.ForcedTermination == true || frontendResult?.ForcedTermination == true ? 8 : 0;
    }
}
