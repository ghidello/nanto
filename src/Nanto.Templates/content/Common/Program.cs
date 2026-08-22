using Nanto;
using Nanto.Generated;
using Nanto.Hosting.Windows;

namespace NantoTemplateApp;

internal static class Program
{
    public static async Task Main()
    {
        var bridge = new NantoBridgeConfiguration();
        bridge.Add(new AppApi());
        NantoContentSource content = CreateContent();
        var options = new NantoApplicationOptions
        {
            ApplicationId = "com.nanto.app-4d5df86c-76c8-4e4d-9572-4680d1a25f58",
            Content = content,
            Bridge = bridge,
            PrimaryWindow = new WindowOptions
            {
                Title = "NantoTemplateApp",
                Capabilities = [AppCapabilities.App.Greet],
            },
        };

        await using var host = new WindowsApplicationHost();
        await host.RunAsync(options);
    }

    private static NantoContentSource CreateContent()
    {
#if DEBUG
        string? developmentUrl = Environment.GetEnvironmentVariable("NANTO_DEVELOPMENT_URL");
        if (Uri.TryCreate(developmentUrl, UriKind.Absolute, out Uri? startUri))
        {
            return new NantoDevelopmentContent { StartUri = startUri };
        }
#endif
        return new NantoProductionContent
        {
            Assets = VersionedWebAssetProvider.FromAssembly<AppAssetMarker>("Nanto.AppAssets.Manifest"),
        };
    }

    private sealed class AppAssetMarker;
}
