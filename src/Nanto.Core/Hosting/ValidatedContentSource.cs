namespace Nanto.Hosting;

public abstract record ValidatedContentSource
{
    private protected ValidatedContentSource()
    {
    }
}

public sealed record ValidatedProductionContent : ValidatedContentSource
{
    public IWebAssetProvider Assets { get; }

    public string InitialRoute { get; }

    internal ValidatedProductionContent(IWebAssetProvider assets, string initialRoute)
    {
        Assets = assets;
        InitialRoute = initialRoute;
    }
}

public sealed record ValidatedDevelopmentContent : ValidatedContentSource
{
    public Uri StartUri { get; }

    public Uri TrustedOrigin { get; }

    internal ValidatedDevelopmentContent(Uri startUri, Uri trustedOrigin)
    {
        StartUri = startUri;
        TrustedOrigin = trustedOrigin;
    }
}
