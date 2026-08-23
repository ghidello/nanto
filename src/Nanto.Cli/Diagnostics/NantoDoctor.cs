using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Nanto.Cli.Configuration;

using Microsoft.Win32;

namespace Nanto.Cli.Diagnostics;

internal static class NantoDoctor
{
    internal static NantoDoctorReport Inspect(ValidatedNantoConfiguration configuration)
    {
        var checks = new List<NantoDoctorCheck>
        {
            Check(
                "platform",
                OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
                OperatingSystem.IsWindows() ? "Windows x64 is required for the current host." : "The current Nanto host supports Windows x64 only.",
                remediation: "Use Windows x64 for the committed MVP host."),
            PathCheck("host-project", configuration.HostProjectPath, "$hostProject", "Set application.hostProject to an existing project inside the application root."),
            PathCheck("frontend-directory", configuration.FrontendDirectory, "$frontend", "Create frontend.directory or correct its configured path."),
        };

        checks.Add(WebView2RuntimeCheck());
        checks.Add(FrontendUrlCheck(configuration));

        AddExecutableCheck(checks, "dotnet", "dotnet", configuration.ApplicationRoot, "Install the .NET SDK pinned by global.json.");
        if (configuration.Value.Frontend.Dev.File is { } frontendExecutable)
        {
            AddExecutableCheck(
                checks,
                "frontend-command",
                frontendExecutable,
                configuration.FrontendDirectory,
                "Install the configured frontend package manager or correct frontend.dev.file.");
        }
        else
        {
            checks.Add(new NantoDoctorCheck
            {
                Name = "frontend-command",
                Status = "external",
                Detail = "The development server is externally managed.",
            });
        }

        return new NantoDoctorReport
        {
            ConfigurationSchema = "v1",
            Platform = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            RuntimeVersion = Environment.Version.ToString(),
            RuntimeProfile = configuration.Value.Build.Runtime,
            Checks = [.. checks],
        };
    }

    internal static string RenderHuman(NantoDoctorReport report)
    {
        var builder = new StringBuilder();
        builder.Append("Nanto doctor (configuration ").Append(report.ConfigurationSchema).AppendLine(")");
        builder.Append("Platform: ").Append(report.Platform).Append(' ').AppendLine(report.Architecture);
        builder.Append("Runtime: ").Append(report.RuntimeVersion).Append("; build profile ").AppendLine(report.RuntimeProfile);
        foreach (NantoDoctorCheck check in report.Checks)
        {
            builder.Append("  [").Append(check.Status).Append("] ").Append(check.Name).Append(": ").AppendLine(check.Detail);
            if (check.Remediation is not null && check.Status != "ok")
            {
                builder.Append("       ").AppendLine(check.Remediation);
            }
        }

        return builder.ToString();
    }

    internal static string RenderJson(NantoDoctorReport report) => JsonSerializer.Serialize(report, NantoDoctorJsonContext.Default.NantoDoctorReport);

    private static void AddExecutableCheck(List<NantoDoctorCheck> checks, string name, string executable, string workingDirectory, string remediation)
    {
        ResolvedExecutable? resolved = ExecutableResolver.Resolve(executable, workingDirectory);
        checks.Add(new NantoDoctorCheck
        {
            Name = name,
            Status = resolved is null ? "missing" : "ok",
            Detail = resolved is null ? "Executable was not found." : $"Executable resolved with the {resolved.LaunchAdapter} adapter.",
            DiagnosticPath = resolved?.Path,
            Remediation = resolved is null ? remediation : null,
        });
    }

    private static NantoDoctorCheck PathCheck(string name, string path, string logicalPath, string remediation) => new()
    {
        Name = name,
        Status = File.Exists(path) || Directory.Exists(path) ? "ok" : "missing",
        Detail = File.Exists(path) || Directory.Exists(path) ? $"{logicalPath} exists." : $"{logicalPath} does not exist.",
        DiagnosticPath = path,
        Remediation = File.Exists(path) || Directory.Exists(path) ? null : remediation,
    };

    private static NantoDoctorCheck Check(string name, bool success, string detail, string remediation) => new()
    {
        Name = name,
        Status = success ? "ok" : "unsupported",
        Detail = detail,
        Remediation = success ? null : remediation,
    };

    private static NantoDoctorCheck FrontendUrlCheck(ValidatedNantoConfiguration configuration)
    {
        bool reachable = IsTcpEndpointReachable(configuration.DevelopmentUri);
        bool managed = configuration.Value.Frontend.Dev.IsManaged;
        return new NantoDoctorCheck
        {
            Name = "frontend-url",
            Status = managed ? reachable ? "occupied" : "ok" : reachable ? "ok" : "unreachable",
            Detail = managed
                ? reachable ? "The managed development endpoint is already occupied." : "The managed development endpoint is available."
                : reachable ? "The externally managed development endpoint is reachable." : "The externally managed development endpoint is not reachable.",
            Remediation = managed && reachable
                ? "Stop the process using frontend.dev.url or configure another explicit port."
                : !managed && !reachable
                    ? "Start the external frontend server at frontend.dev.url before running 'dotnet nanto dev'."
                    : null,
        };
    }

    private static bool IsTcpEndpointReachable(Uri uri)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var client = new TcpClient();
        try
        {
            client.ConnectAsync(uri.Host, uri.Port, timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static NantoDoctorCheck WebView2RuntimeCheck()
    {
        string? version = FindWebView2RuntimeVersion();
        return new NantoDoctorCheck
        {
            Name = "webview2-runtime",
            Status = version is null ? "missing" : "ok",
            Detail = version is null ? "The Evergreen WebView2 Runtime was not detected." : $"Evergreen WebView2 Runtime {version} is installed.",
            Remediation = version is null ? "Install the Evergreen WebView2 Runtime for Windows x64." : null,
        };
    }

    private static string? FindWebView2RuntimeVersion()
    {
        const string clientPath = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? clientKey = baseKey.OpenSubKey(clientPath);
                    if (clientKey?.GetValue("pv") is string version && IsInstalledWebView2Version(version))
                    {
                        return version;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Continue through the documented per-user and per-machine registration locations.
                }
            }
        }

        return null;
    }

    internal static bool IsInstalledWebView2Version(string? value) =>
        Version.TryParse(value, out Version? version) && version > new Version(0, 0, 0, 0);
}
