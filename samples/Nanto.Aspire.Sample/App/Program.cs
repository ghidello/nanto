using Nanto;
using Nanto.Generated;
using Nanto.Hosting.Windows;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Nanto.Aspire.Sample.App;

internal static class Program
{
    public static async Task Main()
    {
        using TracerProvider tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(NantoTelemetry.ActivitySourceName)
            .AddHttpClientInstrumentation()
            .AddOtlpExporter()
            .Build();
        using MeterProvider meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(NantoTelemetry.MeterName)
            .AddOtlpExporter()
            .Build();
        using var dependencyApi = new DependencyApi();
        var bridge = new NantoBridgeConfiguration();
        bridge.Add(dependencyApi);
#if DEBUG
        NantoContentSource content = CreateDevelopmentContent();
#else
        NantoProductionContent content = CreateProductionContent();
#endif
        var options = new NantoApplicationOptions
        {
            ApplicationId = "com.nanto.sample.aspire",
            Content = content,
            Bridge = bridge,
            PrimaryWindow = new WindowOptions
            {
                Title = "Nanto Aspire sample",
                Capabilities = [AppCapabilities.Dependency.Ping],
            },
        };

        await using var host = new WindowsApplicationHost();
        await host.RunAsync(options);
    }

    private static NantoProductionContent CreateProductionContent() => new()
    {
        Assets = VersionedWebAssetProvider.FromAssembly<AppAssetMarker>("Nanto.AppAssets.Manifest"),
    };

#if DEBUG
    private static NantoContentSource CreateDevelopmentContent()
    {
        string? developmentUrl = Environment.GetEnvironmentVariable("NANTO_DEVELOPMENT_URL");
        if (Uri.TryCreate(developmentUrl, UriKind.Absolute, out Uri? startUri))
        {
            return new NantoDevelopmentContent { StartUri = startUri };
        }
        return CreateProductionContent();
    }
#endif

    private sealed class AppAssetMarker;
}
