using System.Collections.Frozen;
using System.Text;

namespace Nanto;

internal static class WebAssetPath
{
    public const string RequiredIndexPath = "/index.html";
    public const string ReservedManifestPath = "/nanto-assets.json";

    public static FrozenSet<string> EnumerateDirectory(DirectoryInfo root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"The web-asset root directory '{root.FullName}' does not exist.");
        }

        ThrowIfReparsePoint(root);
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

                var assetPath = FromFile(root.FullName, file.FullName);
                AddUnique(assetPaths, caseInsensitivePaths, assetPath);
            }
        }

        return assetPaths.ToFrozenSet(StringComparer.Ordinal);
    }

    public static string NormalizeManifestPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] != '/' || path.Length == 1 || path.EndsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The web-asset path '{path}' must have exactly one leading slash and name a file.");
        }

        var segments = path[1..].Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains('\\') || segment.Contains('%')
                || segment.Contains(':') || segment.Contains('?') || segment.Contains('#') || segment.Any(char.IsControl))
            {
                throw new InvalidDataException($"The web-asset path '{path}' contains an unsafe segment.");
            }

            var normalizedSegment = segment.Normalize(NormalizationForm.FormC);
            if (!string.Equals(segment, normalizedSegment, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The web-asset path '{path}' is not normalized to Unicode Form C.");
            }

            segments[index] = normalizedSegment;
        }

        var normalizedPath = $"/{string.Join('/', segments)}";
        if (string.Equals(normalizedPath, ReservedManifestPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The web-asset path '{normalizedPath}' is reserved by Nanto.");
        }

        return normalizedPath;
    }

    public static void AddUnique(HashSet<string> paths, HashSet<string> caseInsensitivePaths, string path)
    {
        if (!paths.Add(path) || !caseInsensitivePaths.Add(path))
        {
            throw new InvalidDataException($"The web-asset path '{path}' is ambiguous on a Windows filesystem.");
        }
    }

    public static void ThrowIfReparsePoint(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The web-asset entry '{entry.FullName}' cannot be a reparse point.");
        }
    }

    public static void EnsureDirectoryTree(string trustedRoot, string directory)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        var canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var relativePath = Path.GetRelativePath(canonicalRoot, canonicalDirectory);
        if (Path.IsPathFullyQualified(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The directory '{canonicalDirectory}' does not resolve beneath '{canonicalRoot}'.");
        }

        var current = new DirectoryInfo(canonicalRoot);
        if (!current.Exists)
        {
            throw new DirectoryNotFoundException($"The trusted web-asset root '{canonicalRoot}' does not exist.");
        }

        ThrowIfReparsePoint(current);
        if (relativePath == ".")
        {
            return;
        }

        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, segment));
            current.Create();
            current.Refresh();
            ThrowIfReparsePoint(current);
        }
    }

    private static string FromFile(string rootDirectory, string filePath)
    {
        var relativePath = Path.GetRelativePath(rootDirectory, filePath);
        if (relativePath == "."
            || Path.IsPathFullyQualified(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The web-asset file '{filePath}' does not resolve beneath '{rootDirectory}'.");
        }

        return NormalizeManifestPath($"/{relativePath.Replace(Path.DirectorySeparatorChar, '/')}");
    }
}
