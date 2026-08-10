namespace Nanto;

public interface IWebAssetLease : IDisposable
{
    string RootDirectory { get; }

    string Version { get; }

    IReadOnlySet<string> AssetPaths { get; }
}