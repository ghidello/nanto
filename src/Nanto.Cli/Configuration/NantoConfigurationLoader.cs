using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nanto.Cli.Configuration;

internal static class NantoConfigurationLoader
{
    private const int SchemaVersion = 1;
    private const string SchemaUri = "https://nanto.dev/schemas/config/v1.json";

    private static readonly HashSet<string> RuntimeValues = new(StringComparer.Ordinal)
    {
        "auto",
        "native-aot",
        "coreclr",
        "coreclr-framework-dependent",
    };

    internal static ValidatedNantoConfiguration Load(string workingDirectory, string? configurationPath, string? environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var applicationRoot = Path.GetFullPath(workingDirectory);
        var path = configurationPath is null
            ? Path.Combine(applicationRoot, "nanto.json")
            : ResolvePath(applicationRoot, configurationPath, "configuration");
        if (!File.Exists(path))
        {
            throw new NantoConfigurationException("Configuration file was not found.");
        }

        var configurationRoot = Path.GetDirectoryName(path) ?? throw new NantoConfigurationException("Configuration path has no parent directory.");
        byte[] bytes = ReadConfiguration(path);
        ValidateNoDuplicateProperties(bytes, "base configuration");
        JsonNode root = ParseObject(bytes, "base configuration");

        if (!string.IsNullOrWhiteSpace(environment))
        {
            ValidateEnvironmentName(environment);
            var overlayPath = Path.Combine(configurationRoot, $"nanto.{environment}.json");
            if (File.Exists(overlayPath))
            {
                byte[] overlayBytes = ReadConfiguration(overlayPath);
                ValidateNoDuplicateProperties(overlayBytes, "environment overlay");
                JsonNode overlay = ParseObject(overlayBytes, "environment overlay");
                MergeObjects(root.AsObject(), overlay.AsObject());
            }
        }

        NantoConfiguration configuration;
        try
        {
            configuration = root.Deserialize(NantoConfigurationJsonContext.Default.NantoConfiguration)
                ?? throw new NantoConfigurationException("Configuration must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new NantoConfigurationException($"Configuration is invalid: {exception.Message}");
        }

        return Validate(configuration, path, configurationRoot);
    }

    private static ValidatedNantoConfiguration Validate(NantoConfiguration configuration, string path, string applicationRoot)
    {
        if (configuration.SchemaVersion != SchemaVersion)
        {
            throw new NantoConfigurationException($"Unsupported configuration schema version '{configuration.SchemaVersion}'. Expected {SchemaVersion}.");
        }

        if (configuration.Schema is not null && !string.Equals(configuration.Schema, SchemaUri, StringComparison.Ordinal))
        {
            throw new NantoConfigurationException($"Unsupported configuration schema URI. Expected '{SchemaUri}'.");
        }

        if (configuration.Application is null || configuration.Frontend is null || configuration.Frontend.Dev is null
            || configuration.Frontend.Build is null || configuration.Build is null)
        {
            throw new NantoConfigurationException("Configuration is missing a required object.");
        }

        string entryAssetValue = configuration.Frontend.Build.EntryAsset ?? "index.html";
        configuration = configuration with
        {
            Frontend = configuration.Frontend with
            {
                Build = configuration.Frontend.Build with { EntryAsset = entryAssetValue },
            },
        };

        var hostProject = ResolveContainedPath(applicationRoot, configuration.Application.HostProject, applicationRoot, "application.hostProject");
        var frontendDirectory = ResolveContainedPath(applicationRoot, configuration.Frontend.Directory, applicationRoot, "frontend.directory");
        var generatedClient = ResolveContainedPath(frontendDirectory, configuration.Frontend.GeneratedClient, frontendDirectory, "frontend.generatedClient");
        var dist = ResolveContainedPath(frontendDirectory, configuration.Frontend.Build.Dist, frontendDirectory, "frontend.build.dist");
        var entryAsset = ResolveContainedPath(dist, entryAssetValue, dist, "frontend.build.entryAsset");

        ValidateCommand(configuration.Frontend.Install, "frontend.install", optional: true);
        ValidateDevelopmentCommand(configuration.Frontend.Dev);
        ValidateCommand(configuration.Frontend.Build, "frontend.build", optional: false);

        if (configuration.Frontend.Dev.ReadyTimeoutSeconds is < 1 or > 600)
        {
            throw new NantoConfigurationException("frontend.dev.readyTimeoutSeconds must be between 1 and 600.");
        }

        if (!Uri.TryCreate(configuration.Frontend.Dev.Url, UriKind.Absolute, out Uri? developmentUri)
            || developmentUri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(developmentUri.Host)
            || !string.IsNullOrEmpty(developmentUri.UserInfo)
            || !HasExplicitPort(configuration.Frontend.Dev.Url))
        {
            throw new NantoConfigurationException("frontend.dev.url must be an absolute HTTP or HTTPS URL with a concrete host and explicit port.");
        }

        if (!RuntimeValues.Contains(configuration.Build.Runtime))
        {
            throw new NantoConfigurationException("build.runtime must be auto, native-aot, coreclr, or coreclr-framework-dependent.");
        }

        return new ValidatedNantoConfiguration
        {
            ConfigurationPath = Path.GetFullPath(path),
            ApplicationRoot = Path.GetFullPath(applicationRoot),
            HostProjectPath = hostProject,
            FrontendDirectory = frontendDirectory,
            GeneratedClientDirectory = generatedClient,
            FrontendDistDirectory = dist,
            FrontendEntryAssetPath = entryAsset,
            DevelopmentUri = developmentUri,
            Value = configuration,
        };
    }

    private static void ValidateCommand(NantoCommandConfiguration? command, string propertyName, bool optional)
    {
        if (command is null)
        {
            if (!optional)
            {
                throw new NantoConfigurationException($"{propertyName} is required.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(command.File) || command.File.Contains('\0'))
        {
            throw new NantoConfigurationException($"{propertyName}.file must be a non-empty executable name or path.");
        }

        if (command.Arguments is null || command.Arguments.Any(static argument => argument is null || argument.Contains('\0')))
        {
            throw new NantoConfigurationException($"{propertyName}.arguments must contain only valid strings.");
        }
    }

    private static void ValidateDevelopmentCommand(NantoDevelopmentConfiguration command)
    {
        if (command.File is null && command.Arguments is null)
        {
            return;
        }

        if (command.File is null || command.Arguments is null)
        {
            throw new NantoConfigurationException("frontend.dev.file and frontend.dev.arguments must either both be present or both be absent.");
        }

        ValidateCommand(new NantoCommandConfiguration { File = command.File, Arguments = command.Arguments }, "frontend.dev", optional: false);
    }

    private static string ResolvePath(string baseDirectory, string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            throw new NantoConfigurationException($"{propertyName} path must not be empty.");
        }

        try
        {
            return Path.GetFullPath(value, baseDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new NantoConfigurationException($"{propertyName} path is invalid.");
        }
    }

    private static string ResolveContainedPath(string baseDirectory, string value, string allowedRoot, string propertyName)
    {
        string path = ResolvePath(baseDirectory, value, propertyName);
        string relative = Path.GetRelativePath(allowedRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new NantoConfigurationException($"{propertyName} must remain inside its configured root.");
        }

        return path;
    }

    private static byte[] ReadConfiguration(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new NantoConfigurationException("Configuration could not be read.");
        }
    }

    private static JsonNode ParseObject(byte[] bytes, string source)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            return node is JsonObject ? node : throw new NantoConfigurationException($"The {source} must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new NantoConfigurationException($"The {source} is invalid JSON: {exception.Message}");
        }
    }

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> bytes, string source)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var objectProperties = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        objectProperties.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        string propertyName = reader.GetString()!;
                        if (!objectProperties.Peek().Add(propertyName))
                        {
                            throw new NantoConfigurationException($"The {source} contains duplicate property '{propertyName}'.");
                        }

                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            throw new NantoConfigurationException($"The {source} is invalid JSON: {exception.Message}");
        }
    }

    private static void MergeObjects(JsonObject target, JsonObject overlay)
    {
        foreach ((string propertyName, JsonNode? overlayValue) in overlay)
        {
            if (overlayValue is JsonObject overlayObject && target[propertyName] is JsonObject targetObject)
            {
                MergeObjects(targetObject, overlayObject);
            }
            else
            {
                target[propertyName] = overlayValue?.DeepClone();
            }
        }
    }

    private static void ValidateEnvironmentName(string environment)
    {
        if (environment is "." or ".." || environment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || environment.Contains(Path.DirectorySeparatorChar) || environment.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new NantoConfigurationException("Environment name must be a single valid file-name segment.");
        }
    }

    private static bool HasExplicitPort(string value)
    {
        int schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return false;
        }

        int authorityStart = schemeSeparator + 3;
        int authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        ReadOnlySpan<char> authority = authorityEnd < 0 ? value.AsSpan(authorityStart) : value.AsSpan(authorityStart, authorityEnd - authorityStart);
        if (authority.StartsWith('['))
        {
            int bracket = authority.IndexOf(']');
            return bracket >= 0 && bracket + 1 < authority.Length && authority[bracket + 1] == ':';
        }

        return authority.LastIndexOf(':') > 0;
    }
}
