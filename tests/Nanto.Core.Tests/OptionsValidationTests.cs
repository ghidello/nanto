using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Nanto.Hosting;

namespace Nanto.Core.Tests;

public sealed class OptionsValidationTests
{
    [Fact]
    public void CreateValidatesAndNormalizesCompleteGraph()
    {
        var options = CreateOptions();

        var validated = ValidatedApplicationOptions.Create(options);

        validated.Identity.CanonicalId.Should().Be("com.example.nanto");
        validated.LoggerFactory.Should().BeSameAs(NullLoggerFactory.Instance);
        validated.PrimaryWindow.Should().BeSameAs(options.PrimaryWindow);
        validated.PreferredColorScheme.Should().Be(ColorSchemePreference.System);
        var applicationRoot = Path.Combine(Path.GetTempPath(), "nanto-options-test");
        var preparationContext = validated.CreateWebAssetPreparationContext(applicationRoot);
        preparationContext.ApplicationId.Should().Be(validated.Identity.CanonicalId);
        preparationContext.ApplicationStorageKey.Should().Be(validated.Identity.StorageKey);
        preparationContext.ApplicationRootDirectory.Should().Be(Path.GetFullPath(applicationRoot));
        preparationContext.LoggerFactory.Should().BeSameAs(validated.LoggerFactory);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("//example.com/path")]
    [InlineData("/a/../b")]
    [InlineData("/a/%2e%2e/b")]
    [InlineData("/a/%2f/b")]
    [InlineData("/a\\b")]
    public void CreateRejectsUnsafeRoutes(string route)
    {
        var options = CreateOptions() with
        {
            PrimaryWindow = CreateOptions().PrimaryWindow with { InitialRoute = route },
        };

        var action = () => ValidatedApplicationOptions.Create(options);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateRejectsInvalidDefaultSize()
    {
        var options = CreateOptions() with
        {
            PrimaryWindow = CreateOptions().PrimaryWindow with { InitialSize = default },
        };

        var action = () => ValidatedApplicationOptions.Create(options);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateRejectsUnknownColorSchemePreference()
    {
        var options = CreateOptions() with
        {
            PreferredColorScheme = (ColorSchemePreference)int.MaxValue,
        };

        var action = () => ValidatedApplicationOptions.Create(options);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static NantoApplicationOptions CreateOptions() => new()
    {
        ApplicationId = "com.example.nanto",
        Assets = new StubAssetProvider(),
        PrimaryWindow = new WindowOptions { Title = "Nanto" },
    };

    private sealed class StubAssetProvider : IWebAssetProvider
    {
        public ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
