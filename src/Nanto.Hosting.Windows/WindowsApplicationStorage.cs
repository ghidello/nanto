using System.Text.Json;

using Nanto.Hosting;

namespace Nanto.Hosting.Windows;

internal sealed class WindowsApplicationStorage
{
    private const int IdentitySchemaVersion = 1;

    public string ApplicationRoot { get; }

    public string UserDataDirectory { get; }

    private WindowsApplicationStorage(string applicationRoot, string userDataDirectory)
    {
        ApplicationRoot = applicationRoot;
        UserDataDirectory = userDataDirectory;
    }

    public static WindowsApplicationStorage Prepare(ApplicationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
        {
            throw new InvalidOperationException("Windows did not provide an absolute local application-data directory.");
        }

        var applicationRoot = Path.Combine(localApplicationData, "Nanto", "applications", identity.StorageKey);
        var userDataParent = Path.Combine(applicationRoot, "profiles", "default");
        var userDataDirectory = Path.Combine(userDataParent, "webview2-udf");
        Directory.CreateDirectory(applicationRoot);
        ProbeWriteAccess(applicationRoot);
        EnsureIdentity(applicationRoot, identity.CanonicalId);
        Directory.CreateDirectory(userDataParent);
        ProbeWriteAccess(userDataParent);
        return new WindowsApplicationStorage(applicationRoot, userDataDirectory);
    }

    private static void EnsureIdentity(string applicationRoot, string applicationId)
    {
        var identityPath = Path.Combine(applicationRoot, "application.json");
        if (File.Exists(identityPath))
        {
            ValidateIdentity(identityPath, applicationId);
            return;
        }

        var temporaryPath = Path.Combine(applicationRoot, $".application-{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new WindowsApplicationIdentityDocument
            {
                SchemaVersion = IdentitySchemaVersion,
                ApplicationId = applicationId,
            };
            var json = JsonSerializer.Serialize(document, WindowsApplicationStorageJsonContext.Default.WindowsApplicationIdentityDocument);
            json += "\n";
            File.WriteAllText(temporaryPath, json);
            try
            {
                File.Move(temporaryPath, identityPath);
            }
            catch (IOException) when (File.Exists(identityPath))
            {
                ValidateIdentity(identityPath, applicationId);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ProbeWriteAccess(string directory)
    {
        var probePath = Path.Combine(directory, $".nanto-write-probe-{Guid.NewGuid():N}");
        try
        {
            using var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Nanto cannot write to application storage at '{directory}'.", exception);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private static void ValidateIdentity(string identityPath, string expectedApplicationId)
    {
        try
        {
            using var stream = File.OpenRead(identityPath);
            var document = JsonSerializer.Deserialize(
                stream,
                WindowsApplicationStorageJsonContext.Default.WindowsApplicationIdentityDocument);
            if (document is null
                || document.SchemaVersion != IdentitySchemaVersion
                || !string.Equals(document.ApplicationId, expectedApplicationId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Application identity metadata at '{identityPath}' does not match '{expectedApplicationId}'.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Application identity metadata at '{identityPath}' is malformed.", exception);
        }
    }
}