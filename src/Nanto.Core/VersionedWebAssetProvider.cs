using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nanto;

/// <summary>
/// Materializes an explicitly declared embedded web-asset manifest into an immutable, content-addressed application cache.
/// </summary>
public sealed class VersionedWebAssetProvider : IWebAssetProvider
{
    private const int ManifestSchemaVersion = 1;
    private const string AssetsDirectoryName = "assets-v1";
    private const string CachedManifestFileName = "manifest.json";
    private const string CompleteFileName = "complete";
    private const string ContentDirectoryName = "content";
    private const string LeaseFileName = "lease.lock";
    private const string MaintenanceFileName = "maintenance.lock";
    private const string QuarantineDirectoryName = "quarantine";
    private const string StagingDirectoryName = "staging";

    private static readonly byte[] _bundleDomain = "NANTO-ASSETS-V1"u8.ToArray();
    private static readonly byte[] _separator = [0];

    private readonly Assembly _assembly;
    private readonly Action<string>? _assetPublished;
    private readonly string _manifestResourceName;

    private VersionedWebAssetProvider(Assembly assembly, string manifestResourceName, Action<string>? assetPublished)
    {
        _assembly = assembly;
        _manifestResourceName = manifestResourceName;
        _assetPublished = assetPublished;
    }

    /// <summary>
    /// Creates a provider that reads a named manifest and its explicitly declared resources from the marker type's assembly.
    /// </summary>
    public static VersionedWebAssetProvider FromAssembly<TMarker>(string manifestResourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestResourceName);
        return new VersionedWebAssetProvider(typeof(TMarker).Assembly, manifestResourceName, null);
    }

    internal static VersionedWebAssetProvider FromAssemblyForTesting<TMarker>(string manifestResourceName, Action<string> assetPublished)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestResourceName);
        ArgumentNullException.ThrowIfNull(assetPublished);
        return new VersionedWebAssetProvider(typeof(TMarker).Assembly, manifestResourceName, assetPublished);
    }

    /// <inheritdoc />
    public ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IWebAssetLease>(Task.Run<IWebAssetLease>(() => Prepare(context, cancellationToken), cancellationToken));
    }

    private static void AcquireMaintenanceMutex(Mutex mutex, CancellationToken cancellationToken)
    {
        try
        {
            var signaled = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]);
            if (signaled != 0)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        catch (AbandonedMutexException)
        {
            // The prior process exited while publishing. The locked validation path below
            // treats any incomplete destination as corruption and safely reconstructs it.
        }
    }

    private static void AddBundleField(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        hash.AppendData(value);
        hash.AppendData(_separator);
    }

    private static void CleanupStaging(string stagingDirectory, Exception creationException)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (Exception cleanupException)
        {
            throw new AggregateException("Web-asset publication failed and its staging directory could not be removed.", creationException, cleanupException);
        }
    }

    private static string ComputeBundleHash(IReadOnlyList<ValidatedAsset> assets)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddBundleField(hash, _bundleDomain);
        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        foreach (var asset in assets)
        {
            AddBundleField(hash, Encoding.UTF8.GetBytes(asset.Path));
            BinaryPrimitives.WriteInt64BigEndian(lengthBytes, asset.Length);
            hash.AppendData(lengthBytes);
            hash.AppendData(asset.HashBytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string CreateMaintenanceMutexName(WebAssetPreparationContext context)
    {
        var scope = $"{Path.GetFullPath(context.ApplicationRootDirectory)}\0{context.ApplicationStorageKey}";
        var scopeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        var prefix = OperatingSystem.IsWindows() ? "Global\\" : string.Empty;
        return $"{prefix}Nanto.Assets.{scopeHash}";
    }

    private static CachedAssetManifestDocument CreateCachedManifest(
        WebAssetPreparationContext context,
        string bundleHash,
        IReadOnlyList<ValidatedAsset> assets) =>
        new()
        {
            SchemaVersion = ManifestSchemaVersion,
            ApplicationId = context.ApplicationId,
            BundleHash = bundleHash,
            Assets = [.. assets.Select(asset => new CachedAssetManifestEntry
            {
                Path = asset.Path,
                Length = asset.Length,
                Sha256 = asset.Hash,
            })],
        };

    private static void EnsureMaintenanceMarker(string assetsRoot)
    {
        var markerPath = Path.Combine(assetsRoot, MaintenanceFileName);
        var markerInfo = new FileInfo(markerPath);
        if (markerInfo.Exists)
        {
            WebAssetPath.ThrowIfReparsePoint(markerInfo);
        }

        try
        {
            using var marker = new FileStream(markerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException($"The asset-cache maintenance marker '{markerPath}' cannot be opened.", exception);
        }
        markerInfo.Refresh();
        WebAssetPath.ThrowIfReparsePoint(markerInfo);
    }

    private static FileStream OpenLease(string bundleDirectory)
    {
        var leasePath = Path.Combine(bundleDirectory, LeaseFileName);
        var lease = new FileInfo(leasePath);
        if (!lease.Exists)
        {
            throw new InvalidDataException($"The asset bundle lease '{leasePath}' is missing.");
        }

        WebAssetPath.ThrowIfReparsePoint(lease);
        return new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static string PathFromAsset(string contentDirectory, string assetPath)
    {
        var relativePath = assetPath[1..].Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(contentDirectory, relativePath));
        var contentPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentDirectory)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The web-asset path '{assetPath}' escapes the content directory.");
        }

        return fullPath;
    }

    private static void Publish(
        Assembly assembly,
        WebAssetPreparationContext context,
        string bundleHash,
        IReadOnlyList<ValidatedAsset> assets,
        Action<string>? assetPublished,
        string stagingRoot,
        string destination,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = Path.Combine(stagingRoot, $"{bundleHash}-{Guid.NewGuid():N}");
        try
        {
            WebAssetPath.EnsureDirectoryTree(stagingRoot, stagingDirectory);
            var contentDirectory = Path.Combine(stagingDirectory, ContentDirectoryName);
            WebAssetPath.EnsureDirectoryTree(stagingDirectory, contentDirectory);
            foreach (var asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = PathFromAsset(contentDirectory, asset.Path);
                var outputDirectory = Path.GetDirectoryName(outputPath)
                    ?? throw new InvalidDataException($"The web-asset path '{asset.Path}' has no parent directory.");
                WebAssetPath.EnsureDirectoryTree(contentDirectory, outputDirectory);
                WriteAsset(assembly, asset, outputPath, cancellationToken);
                assetPublished?.Invoke(asset.Path);
            }

            File.WriteAllBytes(Path.Combine(stagingDirectory, LeaseFileName), []);
            var cachedManifest = CreateCachedManifest(context, bundleHash, assets);
            var manifestJson = JsonSerializer.Serialize(cachedManifest, AssetManifestJsonContext.Default.CachedAssetManifestDocument);
            File.WriteAllText(Path.Combine(stagingDirectory, CachedManifestFileName), $"{manifestJson}\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stagingDirectory, CompleteFileName), $"{bundleHash}\n", new UTF8Encoding(false));
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, destination);
        }
        catch (Exception exception)
        {
            CleanupStaging(stagingDirectory, exception);
            throw;
        }
    }

    private static void Quarantine(string destination, string quarantineRoot, string bundleHash)
    {
        WebAssetPath.EnsureDirectoryTree(Path.GetDirectoryName(quarantineRoot)!, quarantineRoot);
        var quarantinePath = Path.Combine(quarantineRoot, $"{bundleHash}-{Guid.NewGuid():N}");
        Directory.Move(destination, quarantinePath);
    }

    private static CachedAssetManifestDocument ReadCachedManifest(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            return JsonSerializer.Deserialize(stream, AssetManifestJsonContext.Default.CachedAssetManifestDocument)
                ?? throw new InvalidDataException($"The cached asset manifest '{manifestPath}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The cached asset manifest '{manifestPath}' is malformed.", exception);
        }
    }

    private static void ValidateBundle(
        string bundleDirectory,
        WebAssetPreparationContext context,
        string expectedBundleHash,
        IReadOnlyList<ValidatedAsset> expectedAssets,
        CancellationToken cancellationToken)
    {
        var bundle = new DirectoryInfo(bundleDirectory);
        if (!bundle.Exists)
        {
            throw new DirectoryNotFoundException($"The asset bundle '{bundleDirectory}' does not exist.");
        }

        WebAssetPath.ThrowIfReparsePoint(bundle);
        var manifestPath = Path.Combine(bundleDirectory, CachedManifestFileName);
        var manifestFile = new FileInfo(manifestPath);
        if (!manifestFile.Exists)
        {
            throw new InvalidDataException($"The cached asset manifest '{manifestPath}' is missing or is not a regular file.");
        }

        WebAssetPath.ThrowIfReparsePoint(manifestFile);
        var cachedManifest = ReadCachedManifest(manifestPath);
        if (string.IsNullOrWhiteSpace(cachedManifest.ApplicationId))
        {
            throw new InvalidDataException($"The cached asset manifest '{manifestPath}' has no application identity.");
        }

        if (!string.Equals(cachedManifest.ApplicationId, context.ApplicationId, StringComparison.Ordinal))
        {
            throw new AssetIdentityMismatchException(
                $"The asset bundle '{bundleDirectory}' belongs to '{cachedManifest.ApplicationId}', not '{context.ApplicationId}'.");
        }

        ValidateBundleShape(bundle);

        if (cachedManifest.SchemaVersion != ManifestSchemaVersion
            || string.IsNullOrWhiteSpace(cachedManifest.BundleHash)
            || !string.Equals(cachedManifest.BundleHash, expectedBundleHash, StringComparison.Ordinal)
            || cachedManifest.Assets is null
            || cachedManifest.Assets.Length != expectedAssets.Count)
        {
            throw new InvalidDataException($"The cached asset manifest '{manifestPath}' does not match the expected bundle.");
        }

        for (var index = 0; index < expectedAssets.Count; index++)
        {
            var expected = expectedAssets[index];
            var actual = cachedManifest.Assets[index];
            if (actual is null
                || !string.Equals(actual.Path, expected.Path, StringComparison.Ordinal)
                || actual.Length != expected.Length
                || !string.Equals(actual.Sha256, expected.Hash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The cached asset manifest '{manifestPath}' does not match the expected inventory.");
            }
        }

        var completePath = Path.Combine(bundleDirectory, CompleteFileName);
        if (!string.Equals(File.ReadAllText(completePath), $"{expectedBundleHash}\n", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The asset bundle completion marker '{completePath}' is invalid.");
        }

        var contentDirectory = Path.Combine(bundleDirectory, ContentDirectoryName);
        var actualPaths = WebAssetPath.EnumerateDirectory(new DirectoryInfo(contentDirectory), cancellationToken);
        if (actualPaths.Count != expectedAssets.Count || expectedAssets.Any(asset => !actualPaths.Contains(asset.Path)))
        {
            throw new InvalidDataException($"The asset bundle '{bundleDirectory}' does not contain the exact declared file set.");
        }

        foreach (var asset in expectedAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(PathFromAsset(contentDirectory, asset.Path));
            if (file.Length != asset.Length)
            {
                throw new InvalidDataException($"The cached web asset '{asset.Path}' has an unexpected length.");
            }
        }
    }

    private static void ValidateBundleShape(DirectoryInfo bundle)
    {
        var expectedNames = new HashSet<string>(
            [ContentDirectoryName, CachedManifestFileName, CompleteFileName, LeaseFileName],
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in bundle.EnumerateFileSystemInfos())
        {
            WebAssetPath.ThrowIfReparsePoint(entry);
            if (!expectedNames.Remove(entry.Name))
            {
                throw new InvalidDataException($"The asset bundle '{bundle.FullName}' contains the unexpected entry '{entry.Name}'.");
            }

            if (entry.Name.Equals(ContentDirectoryName, StringComparison.OrdinalIgnoreCase) != (entry is DirectoryInfo))
            {
                throw new InvalidDataException($"The asset bundle entry '{entry.FullName}' has the wrong kind.");
            }
        }

        if (expectedNames.Count != 0)
        {
            throw new InvalidDataException($"The asset bundle '{bundle.FullName}' is incomplete.");
        }
    }

    private static void WriteAsset(Assembly assembly, ValidatedAsset asset, string outputPath, CancellationToken cancellationToken)
    {
        using var input = assembly.GetManifestResourceStream(asset.ResourceName)
            ?? throw new InvalidDataException($"The embedded web-asset resource '{asset.ResourceName}' does not exist.");
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            hash.AppendData(buffer.AsSpan(0, read));
            length += read;
        }

        var actualHash = hash.GetHashAndReset();
        if (length != asset.Length || !CryptographicOperations.FixedTimeEquals(actualHash, asset.HashBytes))
        {
            throw new InvalidDataException($"The embedded web asset '{asset.Path}' does not match its declared length and SHA-256.");
        }
    }

    private VersionedWebAssetLease Prepare(WebAssetPreparationContext context, CancellationToken cancellationToken)
    {
        var applicationRoot = new DirectoryInfo(context.ApplicationRootDirectory);
        if (!applicationRoot.Exists)
        {
            throw new DirectoryNotFoundException($"The application storage root '{applicationRoot.FullName}' does not exist.");
        }

        WebAssetPath.ThrowIfReparsePoint(applicationRoot);
        var assets = ReadAndValidateManifest(cancellationToken);
        var bundleHash = ComputeBundleHash(assets);
        var assetsRoot = Path.Combine(applicationRoot.FullName, AssetsDirectoryName);
        var stagingRoot = Path.Combine(assetsRoot, StagingDirectoryName);
        var quarantineRoot = Path.Combine(assetsRoot, QuarantineDirectoryName);
        WebAssetPath.EnsureDirectoryTree(applicationRoot.FullName, assetsRoot);
        WebAssetPath.EnsureDirectoryTree(assetsRoot, stagingRoot);
        WebAssetPath.EnsureDirectoryTree(assetsRoot, quarantineRoot);
        EnsureMaintenanceMarker(assetsRoot);

        using var maintenanceMutex = new Mutex(false, CreateMaintenanceMutexName(context));
        var ownsMutex = false;
        try
        {
            AcquireMaintenanceMutex(maintenanceMutex, cancellationToken);
            ownsMutex = true;
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(assetsRoot, bundleHash);
            if (Directory.Exists(destination))
            {
                try
                {
                    ValidateBundle(destination, context, bundleHash, assets, cancellationToken);
                }
                catch (AssetIdentityMismatchException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Quarantine(destination, quarantineRoot, bundleHash);
                    Publish(_assembly, context, bundleHash, assets, _assetPublished, stagingRoot, destination, cancellationToken);
                }
            }
            else
            {
                Publish(_assembly, context, bundleHash, assets, _assetPublished, stagingRoot, destination, cancellationToken);
            }

            ValidateBundle(destination, context, bundleHash, assets, cancellationToken);
            var lease = OpenLease(destination);
            return new VersionedWebAssetLease(
                Path.Combine(destination, ContentDirectoryName),
                bundleHash,
                assets.Select(asset => asset.Path).ToFrozenSet(StringComparer.Ordinal),
                lease);
        }
        finally
        {
            if (ownsMutex)
            {
                maintenanceMutex.ReleaseMutex();
            }
        }
    }

    private List<ValidatedAsset> ReadAndValidateManifest(CancellationToken cancellationToken)
    {
        AssetManifestDocument manifest;
        try
        {
            using var stream = _assembly.GetManifestResourceStream(_manifestResourceName)
                ?? throw new InvalidDataException($"The embedded web-asset manifest '{_manifestResourceName}' does not exist.");
            manifest = JsonSerializer.Deserialize(stream, AssetManifestJsonContext.Default.AssetManifestDocument)
                ?? throw new InvalidDataException($"The embedded web-asset manifest '{_manifestResourceName}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The embedded web-asset manifest '{_manifestResourceName}' is malformed.", exception);
        }

        if (manifest.SchemaVersion != ManifestSchemaVersion)
        {
            throw new InvalidDataException($"The embedded web-asset manifest '{_manifestResourceName}' has an unsupported schema.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resourceNames = new HashSet<string>(StringComparer.Ordinal);
        if (manifest.Assets is null)
        {
            throw new InvalidDataException($"The embedded web-asset manifest '{_manifestResourceName}' has no asset inventory.");
        }

        var assets = new List<ValidatedAsset>(manifest.Assets.Length);
        foreach (var entry in manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.Path)
                || string.IsNullOrWhiteSpace(entry.ResourceName)
                || string.IsNullOrWhiteSpace(entry.Sha256)
                || entry.Length < 0
                || !IsUppercaseSha256(entry.Sha256))
            {
                throw new InvalidDataException($"The embedded web-asset manifest entry '{entry?.Path}' has an invalid length or SHA-256.");
            }

            var path = WebAssetPath.NormalizeManifestPath(entry.Path);
            WebAssetPath.AddUnique(paths, caseInsensitivePaths, path);
            if (!resourceNames.Add(entry.ResourceName))
            {
                throw new InvalidDataException($"The embedded web-asset resource '{entry.ResourceName}' is declared more than once.");
            }

            using var resource = _assembly.GetManifestResourceStream(entry.ResourceName)
                ?? throw new InvalidDataException($"The embedded web-asset resource '{entry.ResourceName}' does not exist.");
            assets.Add(new ValidatedAsset(path, entry.ResourceName, entry.Length, entry.Sha256, Convert.FromHexString(entry.Sha256)));
        }

        if (!paths.Contains(WebAssetPath.RequiredIndexPath))
        {
            throw new InvalidDataException($"The embedded web-asset manifest must contain '{WebAssetPath.RequiredIndexPath}'.");
        }

        assets.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return assets;
    }

    private static bool IsUppercaseSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private sealed class AssetIdentityMismatchException : IOException
    {
        public AssetIdentityMismatchException(string message)
            : base(message)
        {
        }
    }

    private sealed record ValidatedAsset(string Path, string ResourceName, long Length, string Hash, byte[] HashBytes);

    private sealed class VersionedWebAssetLease : IWebAssetLease
    {
        private FileStream? _lease;

        public string RootDirectory { get; }

        public string Version { get; }

        public IReadOnlySet<string> AssetPaths { get; }

        public VersionedWebAssetLease(string rootDirectory, string version, IReadOnlySet<string> assetPaths, FileStream lease)
        {
            RootDirectory = rootDirectory;
            Version = version;
            AssetPaths = assetPaths;
            _lease = lease;
        }

        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}