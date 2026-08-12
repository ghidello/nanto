namespace Nanto;

/// <summary>
/// Prepares a validated, no-copy view of web assets already present in a directory.
/// </summary>
/// <remarks>
/// A prepared lease fixes the URL-path inventory, but does not copy, hash, watch, or make the underlying file bytes immutable.
/// Applications must not add, remove, or rename files while the lease is active.
/// </remarks>
public sealed class DirectoryWebAssetProvider : IWebAssetProvider
{
    private const string DirectoryVersion = "directory";
    private readonly string _rootDirectory;

    /// <summary>
    /// Initializes a provider for an existing absolute directory.
    /// </summary>
    public DirectoryWebAssetProvider(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The web-asset root directory must be an absolute path.", nameof(rootDirectory));
        }

        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
    }

    /// <inheritdoc />
    public ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IWebAssetLease>(Task.Run<IWebAssetLease>(() => Prepare(cancellationToken), cancellationToken));
    }

    private DirectoryWebAssetLease Prepare(CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(_rootDirectory);
        var assetPaths = WebAssetPath.EnumerateDirectory(root, cancellationToken);
        if (!assetPaths.Contains(WebAssetPath.RequiredIndexPath))
        {
            throw new InvalidDataException($"The web-asset directory '{_rootDirectory}' must contain '{WebAssetPath.RequiredIndexPath}'.");
        }

        return new DirectoryWebAssetLease(_rootDirectory, assetPaths);
    }

    private sealed class DirectoryWebAssetLease : IWebAssetLease
    {
        public string RootDirectory { get; }

        public string Version => DirectoryVersion;

        public IReadOnlySet<string> AssetPaths { get; }

        public DirectoryWebAssetLease(string rootDirectory, IReadOnlySet<string> assetPaths)
        {
            RootDirectory = rootDirectory;
            AssetPaths = assetPaths;
        }

        public void Dispose()
        {
        }
    }
}