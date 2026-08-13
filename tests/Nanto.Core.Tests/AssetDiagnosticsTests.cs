using AwesomeAssertions;

using Microsoft.Extensions.Logging;

namespace Nanto.Core.Tests;

public sealed class AssetDiagnosticsTests
{
    [Fact]
    public void EventCatalogUsesStableUniqueIdsAndExpectedLevels()
    {
        using var factory = new RecordingLoggerFactory();
        var logger = factory.CreateLogger("Nanto.AssetDiagnostics.Tests");

        AssetDiagnostics.PreparationStarted(logger, "storage-key");
        AssetDiagnostics.MaintenanceLockAcquired(logger, "storage-key", 1);
        AssetDiagnostics.BundleReused(logger, "storage-key", "bundle-hash", 2, 1);
        AssetDiagnostics.BundleQuarantined(logger, "storage-key", "bundle-hash", nameof(InvalidDataException));
        AssetDiagnostics.BundlePublished(logger, "storage-key", "bundle-hash", 2, 1);
        AssetDiagnostics.PreparationFailed(logger, "storage-key", "ValidateManifest", nameof(InvalidDataException), -1, 1);

        factory.Entries.Select(entry => entry.EventId.Id).Should().Equal(400, 401, 402, 403, 404, 405);
        factory.Entries.Select(entry => entry.EventId.Id).Should().OnlyHaveUniqueItems();
        factory.Entries.Where(entry => entry.EventId.Id is 400 or 401).Should().OnlyContain(
            entry => entry.Level == LogLevel.Debug);
        factory.Entries.Where(entry => entry.EventId.Id is 402 or 404).Should().OnlyContain(
            entry => entry.Level == LogLevel.Information);
        factory.Entries.Single(entry => entry.EventId.Id == 403).Level.Should().Be(LogLevel.Warning);
        factory.Entries.Single(entry => entry.EventId.Id == 405).Level.Should().Be(LogLevel.Error);
        factory.Entries.Should().OnlyContain(entry => entry.Exception == null);
    }
}
