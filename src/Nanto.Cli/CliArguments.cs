namespace Nanto.Cli;

internal sealed record CliArguments
{
    public required string Command { get; init; }

    public string? ConfigurationPath { get; init; }

    public string? Environment { get; init; }

    public string Configuration { get; init; } = "Debug";

    public string? Runtime { get; init; }

    public string Format { get; init; } = "human";

    public string? Output { get; init; }

    public bool Plan { get; init; }

    public bool HotReload { get; init; } = true;
}

internal static class CliArgumentParser
{
    internal static CliArguments Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            throw new CliUsageException(string.Empty);
        }

        string command = args[0];
        if (command is not ("dev" or "build" or "doctor"))
        {
            throw new CliUsageException($"Unknown command '{command}'.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            if (option is "--plan" or "--no-hot-reload")
            {
                if (!flags.Add(option))
                {
                    throw new CliUsageException($"Option '{option}' was specified more than once.");
                }

                continue;
            }

            if (option is not ("--config" or "--environment" or "--configuration" or "--runtime" or "--format" or "--output"))
            {
                throw new CliUsageException($"Unknown option '{option}'.");
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliUsageException($"Option '{option}' requires a value.");
            }

            if (!values.TryAdd(option, args[++index]))
            {
                throw new CliUsageException($"Option '{option}' was specified more than once.");
            }
        }

        ValidateOptions(command, values.Keys, flags);
        string format = values.GetValueOrDefault("--format") ?? "human";
        if (format is not ("human" or "json"))
        {
            throw new CliUsageException("--format must be human or json.");
        }

        return new CliArguments
        {
            Command = command,
            ConfigurationPath = values.GetValueOrDefault("--config"),
            Environment = values.GetValueOrDefault("--environment"),
            Configuration = values.GetValueOrDefault("--configuration") ?? (command == "build" ? "Release" : "Debug"),
            Runtime = values.GetValueOrDefault("--runtime"),
            Format = format,
            Output = values.GetValueOrDefault("--output"),
            Plan = flags.Contains("--plan"),
            HotReload = !flags.Contains("--no-hot-reload"),
        };
    }

    private static void ValidateOptions(string command, IEnumerable<string> values, HashSet<string> flags)
    {
        var allowed = command switch
        {
            "dev" => new HashSet<string>(["--config", "--environment", "--configuration", "--format"], StringComparer.Ordinal),
            "build" => new HashSet<string>(["--config", "--configuration", "--runtime", "--output", "--format"], StringComparer.Ordinal),
            "doctor" => new HashSet<string>(["--config", "--format"], StringComparer.Ordinal),
            _ => throw new InvalidOperationException(),
        };
        string? invalidValue = values.FirstOrDefault(value => !allowed.Contains(value));
        if (invalidValue is not null)
        {
            throw new CliUsageException($"Option '{invalidValue}' is not valid for {command}.");
        }

        if (command == "doctor" && flags.Count > 0 || command == "build" && flags.Contains("--no-hot-reload"))
        {
            throw new CliUsageException($"One or more flags are not valid for {command}.");
        }
    }
}

internal sealed class CliUsageException(string message) : Exception(message);
