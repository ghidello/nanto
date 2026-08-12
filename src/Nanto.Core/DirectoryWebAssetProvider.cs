using System.Collections.Frozen;
using System.Text;

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
    private const string RequiredIndexPath = "/index.html";
    private const string ReservedManifestPath = "/nanto-assets.json";

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

        var root = new DirectoryInfo(_rootDirectory);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"The web-asset root directory '{_rootDirectory}' does not exist.");
        }

        ThrowIfReparsePoint(root);
        var assetPaths = EnumerateAssetPaths(root, cancellationToken);
        if (!assetPaths.Contains(RequiredIndexPath))
        {
            throw new InvalidDataException($"The web-asset directory '{_rootDirectory}' must contain '{RequiredIndexPath}'.");
        }

        return ValueTask.FromResult<IWebAssetLease>(new DirectoryWebAssetLease(_rootDirectory, assetPaths));
    }

    private static FrozenSet<string> EnumerateAssetPaths(DirectoryInfo root, CancellationToken cancellationToken)
    {
        var assetPaths = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Stack<DirectoryInfo>();
        pendingDirectories.Push(root);

        while (pendingDirectories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfReparsePoint(entry);

                if (entry is DirectoryInfo childDirectory)
                {
                    pendingDirectories.Push(childDirectory);
                    continue;
                }

                if (entry is not FileInfo file)
                {
                    throw new InvalidDataException($"The web-asset entry '{entry.FullName}' is not a regular file or directory.");
                }

                var assetPath = NormalizeAssetPath(root.FullName, file.FullName);
                if (!assetPaths.Add(assetPath) || !caseInsensitivePaths.Add(assetPath))
                {
                    throw new InvalidDataException($"The web-asset path '{assetPath}' is ambiguous on a Windows filesystem.");
                }
            }
        }

        return assetPaths.ToFrozenSet(StringComparer.Ordinal);
    }

    private static string NormalizeAssetPath(string rootDirectory, string filePath)
    {
        var relativePath = Path.GetRelativePath(rootDirectory, filePath);
        if (relativePath == "." || Path.IsPathFullyQualified(relativePath) || relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The web-asset file '{filePath}' does not resolve beneath '{rootDirectory}'.");
        }

        var segments = relativePath.Split(Path.DirectorySeparatorChar);
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains(Path.AltDirectorySeparatorChar)
                || segment.Contains('%') || segment.Contains(':') || segment.Contains('?') || segment.Contains('#')
                || segment.Any(char.IsControl))
            {
                throw new InvalidDataException($"The web-asset file '{filePath}' has an unsafe URL path.");
            }

            var normalizedSegment = segment.Normalize(NormalizationForm.FormC);
            if (!string.Equals(segment, normalizedSegment, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The web-asset file '{filePath}' is not normalized to Unicode Form C.");
            }

            segments[index] = normalizedSegment;
        }

        var assetPath = $"/{string.Join('/', segments)}";
        if (string.Equals(assetPath, ReservedManifestPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The web-asset path '{assetPath}' is reserved by Nanto.");
        }

        return assetPath;
    }

    private static void ThrowIfReparsePoint(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The web-asset entry '{entry.FullName}' cannot be a reparse point.");
        }
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
