using Nanto.Cli.Configuration;
using Nanto.Cli.Diagnostics;
using Nanto.Cli.Execution;
using Nanto.Cli.Planning;

namespace Nanto.Cli;

internal static class Program
{
    internal const int Success = 0;
    internal const int UsageError = 2;
    internal const int ConfigurationError = 3;
    internal const int InternalError = 9;

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            return await RunAsync(args, Directory.GetCurrentDirectory(), Console.Out, Console.Error, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    internal static int Run(string[] args, string workingDirectory, TextWriter output, TextWriter error) =>
        RunAsync(args, workingDirectory, output, error, CancellationToken.None).GetAwaiter().GetResult();

    internal static async Task<int> RunAsync(
        string[] args,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            CliArguments arguments = CliArgumentParser.Parse(args);
            ValidatedNantoConfiguration configuration = NantoConfigurationLoader.Load(
                workingDirectory,
                arguments.ConfigurationPath,
                arguments.Environment);
            return arguments.Command switch
            {
                "dev" when arguments.Plan => RenderPlan(
                    arguments,
                    NantoPlanFactory.CreateDevelopmentPlan(configuration, arguments.Runtime, arguments.HotReload),
                    output),
                "dev" => await new NantoDevelopmentRunner(output).RunAsync(
                    configuration,
                    arguments.Configuration,
                    arguments.HotReload,
                    cancellationToken),
                "build" when arguments.Plan => RenderPlan(
                    arguments,
                    NantoPlanFactory.CreateBuildPlan(configuration, arguments.Runtime, arguments.Configuration, arguments.Output),
                    output),
                "build" => await new NantoBuildRunner(output).RunAsync(
                    configuration,
                    arguments.Configuration,
                    arguments.Runtime,
                    arguments.Output,
                    cancellationToken),
                "doctor" => RenderDoctor(arguments, NantoDoctor.Inspect(configuration), output),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (CliUsageException exception)
        {
            if (!string.IsNullOrWhiteSpace(exception.Message))
            {
                error.WriteLine(exception.Message);
            }

            error.WriteLine(GetUsage());
            return UsageError;
        }
        catch (NantoConfigurationException exception)
        {
            error.WriteLine($"NANTO_CONFIG: {exception.Message}");
            return ConfigurationError;
        }
        catch (NantoExecutionException exception)
        {
            error.WriteLine($"{exception.Code}: step {exception.StepId} failed. {exception.Message}");
            return exception.StepId.StartsWith("frontend.", StringComparison.Ordinal)
                ? 5
                : exception.StepId.StartsWith("assets.", StringComparison.Ordinal)
                    || exception.StepId.StartsWith("artifacts.", StringComparison.Ordinal)
                        ? 7
                        : 6;
        }
        catch (FileNotFoundException)
        {
            error.WriteLine("NANTO_TOOL_MISSING: A required executable was not found.");
            return 4;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("NANTO_CANCELLED: operation cancelled and owned processes stopped.");
            return 8;
        }
        catch (Exception exception)
        {
            error.WriteLine($"NANTO_INTERNAL: {exception.GetType().Name}");
            return InternalError;
        }
    }

    private static int RenderPlan(CliArguments arguments, NantoPlan plan, TextWriter output)
    {
        output.WriteLine(arguments.Format == "json" ? NantoPlanRenderer.RenderJson(plan) : NantoPlanRenderer.RenderHuman(plan));
        return Success;
    }

    private static int RenderDoctor(CliArguments arguments, NantoDoctorReport report, TextWriter output)
    {
        output.WriteLine(arguments.Format == "json" ? NantoDoctor.RenderJson(report) : NantoDoctor.RenderHuman(report));
        return Success;
    }

    private static string GetUsage() => """
        Usage:
          dotnet nanto dev [--config <path>] [--environment <name>] [--configuration <name>] [--no-hot-reload] --plan [--format human|json]
          dotnet nanto build [--config <path>] [--configuration <name>] [--runtime <profile>] [--output <path>] --plan [--format human|json]
          dotnet nanto doctor [--config <path>] [--format human|json]
        """;
}
