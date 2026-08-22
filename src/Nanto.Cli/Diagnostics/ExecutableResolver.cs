namespace Nanto.Cli.Diagnostics;

internal sealed record ResolvedExecutable(string Path, string LaunchAdapter);

internal static class ExecutableResolver
{
    internal static ResolvedExecutable? Resolve(string executable, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar) || Path.IsPathRooted(executable))
        {
            string candidate = Path.GetFullPath(executable, workingDirectory);
            return ResolveCandidate(candidate);
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        string[] extensions = GetExecutableExtensions(executable);
        foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                ResolvedExecutable? resolved = ResolveCandidate(Path.Combine(directory, executable + extension));
                if (resolved is not null)
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    private static string[] GetExecutableExtensions(string executable)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(executable))
        {
            return [string.Empty];
        }

        string? pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        return string.IsNullOrWhiteSpace(pathExtensions)
            ? [".COM", ".EXE", ".BAT", ".CMD"]
            : pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static ResolvedExecutable? ResolveCandidate(string candidate)
    {
        if (!File.Exists(candidate))
        {
            return null;
        }

        string extension = Path.GetExtension(candidate);
        string adapter = OperatingSystem.IsWindows() && extension is ".cmd" or ".bat" or ".CMD" or ".BAT"
            ? "windows-command-shim"
            : "direct";
        return new(Path.GetFullPath(candidate), adapter);
    }
}
