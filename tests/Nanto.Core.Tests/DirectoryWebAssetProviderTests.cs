using AwesomeAssertions;

namespace Nanto.Core.Tests;

public sealed class DirectoryWebAssetProviderTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"nanto-directory-assets-{Guid.NewGuid():N}");

    [Fact]
    public async Task PrepareAsyncReturnsNormalizedFixedInventory()
    {
        Directory.CreateDirectory(Path.Combine(_rootDirectory, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_rootDirectory, "index.html"), "<html></html>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_rootDirectory, "..bundle.js"), "export {};", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_rootDirectory, "assets", "app.js"),
            "export {};",
            TestContext.Current.CancellationToken);
        var provider = new DirectoryWebAssetProvider(_rootDirectory);

        using var lease = await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        lease.RootDirectory.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_rootDirectory)));
        lease.Version.Should().Be("directory");
        lease.AssetPaths.Should().BeEquivalentTo(["/index.html", "/..bundle.js", "/assets/app.js"]);

        await File.WriteAllTextAsync(Path.Combine(_rootDirectory, "later.js"), "export {};", TestContext.Current.CancellationToken);
        lease.AssetPaths.Should().NotContain("/later.js");
    }

    [Fact]
    public async Task PrepareAsyncRejectsMissingIndex()
    {
        Directory.CreateDirectory(_rootDirectory);
        await File.WriteAllTextAsync(Path.Combine(_rootDirectory, "app.js"), "export {};", TestContext.Current.CancellationToken);
        var provider = new DirectoryWebAssetProvider(_rootDirectory);

        var action = async () => await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public void ConstructorRejectsRelativeRoot()
    {
        var action = () => new DirectoryWebAssetProvider("relative-assets");

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task PrepareAsyncHonorsCancellationBeforeEnumeration()
    {
        Directory.CreateDirectory(_rootDirectory);
        await File.WriteAllTextAsync(Path.Combine(_rootDirectory, "index.html"), "<html></html>", TestContext.Current.CancellationToken);
        var provider = new DirectoryWebAssetProvider(_rootDirectory);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var action = async () => await provider.PrepareAsync(CreateContext(), cancellationSource.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("unsafe%name.js")]
    [InlineData("not-normalized-e\u0301.js")]
    [InlineData("nanto-assets.json")]
    public async Task PrepareAsyncRejectsUnsafeOrReservedUrlPaths(string fileName)
    {
        Directory.CreateDirectory(_rootDirectory);
        await File.WriteAllTextAsync(Path.Combine(_rootDirectory, "index.html"), "<html></html>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_rootDirectory, fileName), "invalid", TestContext.Current.CancellationToken);
        var provider = new DirectoryWebAssetProvider(_rootDirectory);

        var action = async () => await provider.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private static WebAssetPreparationContext CreateContext()
    {
        var options = new NantoApplicationOptions
        {
            ApplicationId = "com.example.assets",
            Assets = new UnusedAssetProvider(),
            PrimaryWindow = new WindowOptions { Title = "Assets" },
        };
        return Nanto.Hosting.ValidatedApplicationOptions.Create(options).CreateWebAssetPreparationContext(Path.GetTempPath());
    }

    private sealed class UnusedAssetProvider : IWebAssetProvider
    {
        public ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
