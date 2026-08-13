using Microsoft.Extensions.Logging;

namespace Nanto;

internal static partial class AssetDiagnostics
{
    [LoggerMessage(400, LogLevel.Debug, "Asset preparation started for storage {StorageId}.")]
    public static partial void PreparationStarted(ILogger logger, string storageId);

    [LoggerMessage(401, LogLevel.Debug, "Asset maintenance lock acquired for storage {StorageId} after {ElapsedMilliseconds} ms.")]
    public static partial void MaintenanceLockAcquired(ILogger logger, string storageId, double elapsedMilliseconds);

    [LoggerMessage(402, LogLevel.Information, "Asset bundle {BundleHash} with {AssetCount} assets was reused for storage {StorageId} after {ElapsedMilliseconds} ms.")]
    public static partial void BundleReused(ILogger logger, string storageId, string bundleHash, int assetCount, double elapsedMilliseconds);

    [LoggerMessage(403, LogLevel.Warning, "Asset bundle {BundleHash} for storage {StorageId} was quarantined because of {Reason}.")]
    public static partial void BundleQuarantined(ILogger logger, string storageId, string bundleHash, string reason);

    [LoggerMessage(404, LogLevel.Information, "Asset bundle {BundleHash} with {AssetCount} assets was published for storage {StorageId} after {ElapsedMilliseconds} ms.")]
    public static partial void BundlePublished(ILogger logger, string storageId, string bundleHash, int assetCount, double elapsedMilliseconds);

    [LoggerMessage(405, LogLevel.Error, "Asset preparation failed for storage {StorageId} during {Operation} with {ExceptionType} and code {ErrorCode} after {ElapsedMilliseconds} ms.")]
    public static partial void PreparationFailed(
        ILogger logger,
        string storageId,
        string operation,
        string exceptionType,
        int errorCode,
        double elapsedMilliseconds);
}
