using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.WinTrust;

namespace Nanto.Hosting.Windows.IntegrationTestKit;

public static class Phase1DeploymentInspector
{
    private const int ImportDescriptorSize = 20;
    private const int MaximumImportDescriptorCount = 4096;
    private const int MaximumImportNameByteCount = 260;

    private static readonly DateTimeOffset _deterministicZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid _wintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static Phase1DeploymentInspection InspectDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Deployment directory '{root}' does not exist.");
        }

        EnsureNotReparsePoint(root);
        var files = new List<Phase1DeploymentFile>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.TryPop(out var directoryToInspect))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directoryToInspect))
            {
                EnsureNotReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    pendingDirectories.Push(entry);
                    continue;
                }

                if (!File.Exists(entry))
                {
                    throw new InvalidDataException("The deployment contains an entry that is neither a regular file nor a directory.");
                }

                var file = new FileInfo(entry);
                files.Add(new Phase1DeploymentFile
                {
                    RelativePath = Path.GetRelativePath(root, entry).Replace('\\', '/'),
                    Length = file.Length,
                    Sha256 = ComputeSha256(entry),
                });
            }
        }

        var orderedFiles = files.OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray();
        return new Phase1DeploymentInspection
        {
            Files = orderedFiles,
            TotalBytes = orderedFiles.Sum(static file => file.Length),
        };
    }

    public static Phase1DeploymentFile CreateDeterministicPackage(
        string deploymentDirectory,
        string packagePath,
        IReadOnlyList<Phase1DeploymentFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.GetFullPath(deploymentDirectory);
        var fullPackagePath = Path.GetFullPath(packagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPackagePath)!);

        using (var stream = new FileStream(fullPackagePath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var file in files.OrderBy(static file => file.RelativePath, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.SmallestSize);
                entry.LastWriteTime = _deterministicZipTimestamp;
                using var entryStream = entry.Open();
                using var source = File.OpenRead(Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                source.CopyTo(entryStream);
            }
        }

        var package = new FileInfo(fullPackagePath);
        return new Phase1DeploymentFile
        {
            RelativePath = package.Name,
            Length = package.Length,
            Sha256 = ComputeSha256(fullPackagePath),
        };
    }

    public static long ReadDeclaredAssetBytes(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        return document.RootElement.GetProperty("assets")
            .EnumerateArray()
            .Sum(static asset => asset.GetProperty("length").GetInt64());
    }

    public static string[] ReadPortableExecutableImports(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        using var stream = File.OpenRead(executablePath);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var importDirectory = peReader.PEHeaders.PEHeader?.ImportTableDirectory
            ?? throw new InvalidDataException("The deployment executable does not contain a PE header.");
        if (importDirectory.RelativeVirtualAddress == 0 || importDirectory.Size < ImportDescriptorSize)
        {
            return [];
        }

        var descriptorReader = peReader.GetSectionData(importDirectory.RelativeVirtualAddress).GetReader();
        var descriptorCount = Math.Min(importDirectory.Size / ImportDescriptorSize, MaximumImportDescriptorCount);
        var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < descriptorCount && descriptorReader.RemainingBytes >= ImportDescriptorSize; index++)
        {
            var originalFirstThunk = descriptorReader.ReadUInt32();
            var timeDateStamp = descriptorReader.ReadUInt32();
            var forwarderChain = descriptorReader.ReadUInt32();
            var nameRva = descriptorReader.ReadUInt32();
            var firstThunk = descriptorReader.ReadUInt32();
            if (originalFirstThunk == 0 && timeDateStamp == 0 && forwarderChain == 0 && nameRva == 0 && firstThunk == 0)
            {
                break;
            }

            if (nameRva == 0)
            {
                throw new InvalidDataException("The deployment executable contains an import descriptor without a name.");
            }

            var nameReader = peReader.GetSectionData(checked((int)nameRva)).GetReader();
            var availableNameBytes = Math.Min(nameReader.RemainingBytes, MaximumImportNameByteCount + 1);
            var terminatorOffset = nameReader.IndexOf(0);
            if (terminatorOffset < 1 || terminatorOffset >= availableNameBytes)
            {
                throw new InvalidDataException("The deployment executable contains an invalid or overlong import name.");
            }

            imports.Add(nameReader.ReadUTF8(terminatorOffset));
        }

        return imports.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool HasManagedMetadata(string portableExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portableExecutablePath);
        using var stream = File.OpenRead(portableExecutablePath);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        return peReader.HasMetadata;
    }

    public static Phase1DeploymentIdentity ReadSelfContainedDeploymentIdentity(
        string executablePath,
        string runtimeConfigurationPath,
        string dependencyManifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeConfigurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyManifestPath);

        using var runtimeConfiguration = JsonDocument.Parse(File.ReadAllBytes(runtimeConfigurationPath));
        var targetFramework = runtimeConfiguration.RootElement
            .GetProperty("runtimeOptions")
            .GetProperty("tfm")
            .GetString();
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            throw new InvalidDataException("The runtime configuration does not declare a target framework.");
        }

        using var dependencyManifest = JsonDocument.Parse(File.ReadAllBytes(dependencyManifestPath));
        var runtimeTarget = dependencyManifest.RootElement
            .GetProperty("runtimeTarget")
            .GetProperty("name")
            .GetString();
        var separatorIndex = runtimeTarget?.LastIndexOf('/') ?? -1;
        if (separatorIndex < 0 || separatorIndex == runtimeTarget!.Length - 1)
        {
            throw new InvalidDataException("The dependency manifest does not declare a runtime identifier.");
        }

        return new Phase1DeploymentIdentity
        {
            TargetFramework = targetFramework,
            RuntimeIdentifier = runtimeTarget[(separatorIndex + 1)..],
            Architecture = ReadPortableExecutableArchitecture(executablePath),
        };
    }

    public static string ReadPortableExecutableArchitecture(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        using var executableStream = File.OpenRead(executablePath);
        using var peReader = new PEReader(executableStream, PEStreamOptions.LeaveOpen);
        return peReader.PEHeaders.CoffHeader.Machine switch
        {
            Machine.Amd64 => "X64",
            Machine.Arm64 => "Arm64",
            Machine.I386 => "X86",
            var machine => machine.ToString(),
        };
    }

    public static unsafe bool IsAuthenticodeSignatureTrusted(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        fixed (char* path = fullPath)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = path,
            };
            var trustData = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
                fdwRevocationChecks = WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE,
                dwUnionChoice = WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE,
                dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_VERIFY,
                dwProvFlags = WINTRUST_DATA_PROVIDER_FLAGS.WTD_REVOCATION_CHECK_NONE
                    | WINTRUST_DATA_PROVIDER_FLAGS.WTD_CACHE_ONLY_URL_RETRIEVAL,
                dwUIContext = WINTRUST_DATA_UICONTEXT.WTD_UICONTEXT_EXECUTE,
            };
            trustData.pFile = &fileInfo;

            var action = _wintrustActionGenericVerifyV2;
            var result = PInvoke.WinVerifyTrust(default(HWND), ref action, &trustData);
            trustData.dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
            _ = PInvoke.WinVerifyTrust(default(HWND), ref action, &trustData);
            return result == 0;
        }
    }

    public static string? GetCompanyName(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return FileVersionInfo.GetVersionInfo(Path.GetFullPath(filePath)).CompanyName;
    }

    public static void WriteEvidence(string evidencePath, Phase1DeploymentEvidence evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
        ArgumentNullException.ThrowIfNull(evidence);
        var fullPath = Path.GetFullPath(evidencePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, JsonSerializer.SerializeToUtf8Bytes(evidence, Phase1DeploymentJsonContext.Default.Phase1DeploymentEvidence));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Deployment evidence refuses reparse points.");
        }
    }
}
