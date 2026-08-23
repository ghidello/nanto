using System.Diagnostics;
using System.Text;

using Nanto.Cli.Diagnostics;

namespace Nanto.Cli.Processes;

internal static class CommandLauncher
{
    internal static ProcessStartInfo Create(ProcessCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ResolvedExecutable resolved = ExecutableResolver.Resolve(command.File, command.WorkingDirectory)
            ?? throw new FileNotFoundException("Configured executable was not found.", command.File);
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = !WindowsProcessLauncher.HasConsole,
        };

        if (resolved.LaunchAdapter == "windows-command-shim")
        {
            string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            startInfo.FileName = commandInterpreter;
            startInfo.Arguments = $"/d /s /c \"{BuildWindowsShimCommand(resolved.Path, command.Arguments)}\"";
        }
        else
        {
            startInfo.FileName = resolved.Path;
            foreach (string argument in command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        foreach ((string name, string? value) in command.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    internal static string BuildWindowsShimCommand(string executable, IReadOnlyList<string> arguments)
    {
        var command = new StringBuilder();
        AppendWindowsShimToken(command, executable);
        foreach (string argument in arguments)
        {
            command.Append(' ');
            AppendWindowsShimToken(command, argument);
        }

        return command.ToString();
    }

    private static void AppendWindowsShimToken(StringBuilder builder, string value)
    {
        if (value.IndexOfAny(['\0', '\r', '\n', '"', '%']) >= 0)
        {
            throw new ArgumentException("Windows command-shim arguments cannot contain NUL, newlines, quotes, or percent expansion.", nameof(value));
        }

        builder.Append('"').Append(value).Append('"');
    }
}
