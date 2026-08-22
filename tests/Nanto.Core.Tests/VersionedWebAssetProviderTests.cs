using AwesomeAssertions;

using Microsoft.Extensions.Logging;

namespace Nanto.Core.Tests;

public sealed class VersionedWebAssetProviderTests : IDisposable
{
    private const string ValidManifest = "Nanto.Core.Tests.Manifests.valid.json";

    private readonly string _applicationRoot = Path.Combine(Path.GetTempPath(), $"nanto-versioned-assets-{Guid.NewGuid():N}");

    [Fact]
    public async Task PrepareAsyncLogsPublicationReuseAndQuarantineWithoutSensitivePaths()
    {
        Directory.CreateDirectory(_applicationRoot);
        using var loggerFactory = new RecordingLoggerFactory();
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        string contentDirectory;
        using (var published = await provider.PrepareAsync(CreateContext(loggerFactory), TestContext.Current.CancellationToken))
        {
            contentDirectory = published.RootDirectory;
        }

        using (await provider.PrepareAsync(CreateContext(loggerFactory), TestContext.Current.CancellationToken))
        {
        }

        File.Delete(Path.Combine(contentDirectory, "assets", "app.js"));
        using (await provider.PrepareAsync(CreateContext(loggerFactory), TestContext.Current.CancellationToken))
        {
        }

        loggerFactory.Entries.Select(entry => entry.EventId.Id).Should().Contain([400, 401, 402, 403, 404]);
        loggerFactory.Entries.Where(entry => entry.EventId.Id == 402 || entry.EventId.Id == 404).Should().OnlyContain(
            entry => entry.Level == LogLevel.Information);
        loggerFactory.Entries.Single(entry => entry.EventId.Id == 403).Level.Should().Be(LogLevel.Warning);
        loggerFactory.Entries.Should().OnlyContain(entry => entry.Exception == null);
        loggerFactory.Entries.Select(entry => entry.Message).Should().OnlyContain(
            message => !message.Contains(_applicationRoot, StringComparison.OrdinalIgnoreCase)
                && !message.Contains("versioned-assets", StringComparison.Ordinal)
                && !message.Contains("/index.html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareAsyncPublishesDeterministicBundleAndLease()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);

        using var lease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        lease.Version.Should().MatchRegex("^[0-9A-F]{64}$");
        lease.AssetPaths.Should().BeEquivalentTo(["/index.html", "/assets/app.js"]);
        File.ReadAllText(Path.Combine(lease.RootDirectory, "index.html")).Should().Be("<h1>Nanto</h1>\n");
        File.ReadAllText(Path.Combine(lease.RootDirectory, "assets", "app.js")).Should().Be("export const nanto = true;\n");
        File.Exists(Path.Combine(Directory.GetParent(lease.RootDirectory)!.FullName, "complete")).Should().BeTrue();
        File.Exists(Path.Combine(_applicationRoot, "assets-v1", "maintenance.lock")).Should().BeTrue();
    }

    [Fact]
    public async Task PrepareAsyncComputesIdentityIndependentlyOfManifestAndResourceOrdering()
    {
        Directory.CreateDirectory(_applicationRoot);
        var firstProvider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        var equivalentProvider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(
            "Nanto.Core.Tests.Manifests.equivalent.json");

        using var firstLease = await firstProvider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);
        using var equivalentLease = await equivalentProvider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        equivalentLease.Version.Should().Be(firstLease.Version);
        equivalentLease.RootDirectory.Should().Be(firstLease.RootDirectory);
    }

    [Fact]
    public async Task PrepareAsyncReusesValidBundleWithoutRewritingIt()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        string bundleDirectory;
        DateTime manifestWriteTime;
        using (var firstLease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken))
        {
            bundleDirectory = Directory.GetParent(firstLease.RootDirectory)!.FullName;
            manifestWriteTime = File.GetLastWriteTimeUtc(Path.Combine(bundleDirectory, "manifest.json"));
        }

        using var secondLease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        Directory.GetParent(secondLease.RootDirectory)!.FullName.Should().Be(bundleDirectory);
        File.GetLastWriteTimeUtc(Path.Combine(bundleDirectory, "manifest.json")).Should().Be(manifestWriteTime);
        Directory.EnumerateFileSystemEntries(Path.Combine(_applicationRoot, "assets-v1", "quarantine")).Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsyncAllowsConcurrentSharedLeases()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);

        var leases = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken)));
        var bundleDirectory = Directory.GetParent(leases[0].RootDirectory)!.FullName;
        var leaseFile = Path.Combine(bundleDirectory, "lease.lock");
        try
        {
            leases.Select(lease => lease.Version).Distinct(StringComparer.Ordinal).Should().ContainSingle();
            var delete = () => File.Delete(leaseFile);
            delete.Should().Throw<IOException>();
            var rename = () => Directory.Move(bundleDirectory, $"{bundleDirectory}-moved");
            rename.Should().Throw<IOException>();
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        File.Delete(leaseFile);
        File.Exists(leaseFile).Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsyncQuarantinesLengthCorruptionAndReconstructs()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        string corruptedBundle;
        using (var lease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken))
        {
            corruptedBundle = Directory.GetParent(lease.RootDirectory)!.FullName;
        }

        File.WriteAllText(Path.Combine(corruptedBundle, "content", "assets", "app.js"), "corrupt");

        using var reconstructed = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        File.ReadAllText(Path.Combine(reconstructed.RootDirectory, "assets", "app.js")).Should().Be("export const nanto = true;\n");
        Directory.EnumerateDirectories(Path.Combine(_applicationRoot, "assets-v1", "quarantine")).Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrepareAsyncQuarantinesUnexpectedOrMissingContentAndReconstructs(bool addUnexpectedFile)
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        string contentDirectory;
        using (var lease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken))
        {
            contentDirectory = lease.RootDirectory;
        }

        if (addUnexpectedFile)
        {
            await File.WriteAllTextAsync(
                Path.Combine(contentDirectory, "unexpected.txt"),
                "unexpected",
                TestContext.Current.CancellationToken);
        }
        else
        {
            File.Delete(Path.Combine(contentDirectory, "assets", "app.js"));
        }

        using var reconstructed = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        reconstructed.AssetPaths.Should().BeEquivalentTo(["/index.html", "/assets/app.js"]);
        Directory.EnumerateDirectories(Path.Combine(_applicationRoot, "assets-v1", "quarantine")).Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareAsyncFailsWithoutReconstructionWhenActiveLeasePreventsQuarantine()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        using var activeLease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);
        var bundleDirectory = Directory.GetParent(activeLease.RootDirectory)!.FullName;
        File.WriteAllText(Path.Combine(activeLease.RootDirectory, "assets", "app.js"), "corrupt");

        var action = async () => await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<IOException>();
        Directory.Exists(bundleDirectory).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(_applicationRoot, "assets-v1", "quarantine")).Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsyncRefusesIdentityMismatchWithoutQuarantine()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        string bundleDirectory;
        using (var lease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken))
        {
            bundleDirectory = Directory.GetParent(lease.RootDirectory)!.FullName;
        }

        var manifestPath = Path.Combine(bundleDirectory, "manifest.json");
        var manifest = File.ReadAllText(manifestPath).Replace(
            "com.example.versioned-assets",
            "com.example.another-application",
            StringComparison.Ordinal);
        File.WriteAllText(manifestPath, manifest);
        File.WriteAllText(Path.Combine(bundleDirectory, "unexpected-entry"), "corrupt shape");

        var action = async () => await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<IOException>().WithMessage("*belongs to*another-application*");
        Directory.Exists(bundleDirectory).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(_applicationRoot, "assets-v1", "quarantine")).Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsyncQuarantinesMalformedCachedIdentityAndReconstructs()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        string bundleDirectory;
        using (var lease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken))
        {
            bundleDirectory = Directory.GetParent(lease.RootDirectory)!.FullName;
        }

        var manifestPath = Path.Combine(bundleDirectory, "manifest.json");
        var manifest = File.ReadAllText(manifestPath).Replace(
            "\"applicationId\": \"com.example.versioned-assets\"",
            "\"applicationId\": null",
            StringComparison.Ordinal);
        File.WriteAllText(manifestPath, manifest);

        using var reconstructed = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        File.ReadAllText(Path.Combine(Directory.GetParent(reconstructed.RootDirectory)!.FullName, "manifest.json"))
            .Should().Contain("com.example.versioned-assets");
        Directory.EnumerateDirectories(Path.Combine(_applicationRoot, "assets-v1", "quarantine")).Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareAsyncStructuralReuseDoesNotRehashSameLengthContent()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        string assetPath;
        using (var lease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken))
        {
            assetPath = Path.Combine(lease.RootDirectory, "index.html");
        }

        File.WriteAllText(assetPath, "<h1>Other</h1>\n");

        using var reused = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        File.ReadAllText(Path.Combine(reused.RootDirectory, "index.html")).Should().Be("<h1>Other</h1>\n");
        Directory.EnumerateFileSystemEntries(Path.Combine(_applicationRoot, "assets-v1", "quarantine")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("duplicate-property")]
    [InlineData("unknown-property")]
    [InlineData("unsupported-schema")]
    [InlineData("invalid-hash")]
    [InlineData("missing-index")]
    [InlineData("missing-member")]
    [InlineData("case-collision")]
    [InlineData("content-mismatch")]
    [InlineData("duplicate-path")]
    [InlineData("duplicate-resource")]
    [InlineData("missing-resource")]
    [InlineData("negative-length")]
    [InlineData("null-assets")]
    [InlineData("null-entry")]
    public async Task PrepareAsyncRejectsInvalidManifestOrContent(string manifestName)
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(
            $"Nanto.Core.Tests.Manifests.{manifestName}.json");

        var action = async () => await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task PrepareAsyncHonorsPreCancellationWithoutCreatingCache()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var action = async () => await provider.PrepareAsync(CreateContext(), cancellationSource.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Path.Combine(_applicationRoot, "assets-v1")).Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsyncRemovesOwnedStagingAfterMidPublicationCancellation()
    {
        Directory.CreateDirectory(_applicationRoot);
        using var cancellationSource = new CancellationTokenSource();
        var provider = VersionedWebAssetProvider.FromAssemblyForTesting<VersionedWebAssetProviderTests>(
            ValidManifest,
            _ => cancellationSource.Cancel());

        var action = async () => await provider.PrepareAsync(CreateContext(), cancellationSource.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        Directory.EnumerateFileSystemEntries(Path.Combine(_applicationRoot, "assets-v1", "staging")).Should().BeEmpty();
        Directory.EnumerateDirectories(Path.Combine(_applicationRoot, "assets-v1"))
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo(["staging", "quarantine"]);
    }

    [Fact]
    public async Task PrepareAsyncRemovesOwnedStagingAfterInjectedIoFailure()
    {
        Directory.CreateDirectory(_applicationRoot);
        var provider = VersionedWebAssetProvider.FromAssemblyForTesting<VersionedWebAssetProviderTests>(
            ValidManifest,
            _ => throw new IOException("Injected publication failure."));

        var action = async () => await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<IOException>().WithMessage("Injected publication failure.");
        Directory.EnumerateFileSystemEntries(Path.Combine(_applicationRoot, "assets-v1", "staging")).Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsyncHonorsCancellationWhileWaitingForMaintenanceMutex()
    {
        Directory.CreateDirectory(_applicationRoot);
        using var publishing = new ManualResetEventSlim();
        using var releasePublisher = new ManualResetEventSlim();
        var blockingProvider = VersionedWebAssetProvider.FromAssemblyForTesting<VersionedWebAssetProviderTests>(
            ValidManifest,
            _ =>
            {
                publishing.Set();
                releasePublisher.Wait();
            });
        var firstPreparation = blockingProvider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken).AsTask();
        publishing.Wait(TestContext.Current.CancellationToken);
        using var cancellationSource = new CancellationTokenSource();
        var waitingPreparation = VersionedWebAssetProvider.FromAssembly<VersionedWebAssetProviderTests>(ValidManifest)
            .PrepareAsync(CreateContext(), cancellationSource.Token)
            .AsTask();

        cancellationSource.Cancel();
        var action = async () => await waitingPreparation;

        try
        {
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            releasePublisher.Set();
        }

        using var lease = await firstPreparation;
    }

    public void Dispose()
    {
        if (Directory.Exists(_applicationRoot))
        {
            Directory.Delete(_applicationRoot, recursive: true);
        }
    }

    private WebAssetPreparationContext CreateContext(ILoggerFactory? loggerFactory = null)
    {
        var options = new NantoApplicationOptions
        {
            ApplicationId = "com.example.versioned-assets",
            Content = new NantoProductionContent { Assets = new UnusedAssetProvider() },
            LoggerFactory = loggerFactory,
            PrimaryWindow = new WindowOptions { Title = "Assets" },
        };
        return Nanto.Hosting.ValidatedApplicationOptions.Create(options).CreateWebAssetPreparationContext(_applicationRoot);
    }

    private sealed class UnusedAssetProvider : IWebAssetProvider
    {
        public ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
