namespace Nanto.Cli.Tests;

internal sealed class TemporaryNantoProject : IDisposable
{
    internal string Root { get; }

    internal TemporaryNantoProject(string? configuration = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "nanto-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Write("nanto.json", configuration ?? ValidConfiguration);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal string Write(string relativePath, string contents)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    internal const string ValidConfiguration = """
        {
          "$schema": "https://nanto.dev/schemas/config/v1.json",
          "schemaVersion": 1,
          "application": {
            "hostProject": "MyApp.csproj"
          },
          "frontend": {
            "directory": "Frontend",
            "generatedClient": "src/generated/nanto",
            "install": {
              "file": "npm",
              "arguments": ["ci"]
            },
            "dev": {
              "file": "npm",
              "arguments": ["run", "dev"],
              "url": "http://127.0.0.1:5173",
              "readyTimeoutSeconds": 60
            },
            "build": {
              "file": "npm",
              "arguments": ["run", "build"],
              "dist": "dist"
            }
          },
          "build": {
            "runtime": "auto"
          }
        }
        """;
}
