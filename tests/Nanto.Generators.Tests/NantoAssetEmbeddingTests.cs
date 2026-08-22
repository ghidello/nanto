using System.Security.Cryptography;
using System.Text.Json;

using AwesomeAssertions;

using Nanto.Sdk.AssetFixture;

namespace Nanto.Generators.Tests;

public sealed class NantoAssetEmbeddingTests
{
    [Fact]
    public void SdkTargetEmbedsManifestAndEveryContentAddressedAsset()
    {
        System.Reflection.Assembly assembly = typeof(AssetFixtureMarker).Assembly;
        string[] resourceNames = assembly.GetManifestResourceNames();
        resourceNames.Should().Contain("Nanto.Sdk.AssetFixture.Manifest");
        using Stream manifestStream = assembly.GetManifestResourceStream("Nanto.Sdk.AssetFixture.Manifest")!;
        using JsonDocument manifest = JsonDocument.Parse(manifestStream);
        JsonElement[] assets = manifest.RootElement.GetProperty("assets").EnumerateArray().ToArray();

        assets.Select(static asset => asset.GetProperty("path").GetString()).Should().Equal("/assets/app.js", "/index.html");
        foreach (JsonElement asset in assets)
        {
            string resourceName = asset.GetProperty("resourceName").GetString()!;
            resourceNames.Should().Contain(resourceName);
            using Stream assetStream = assembly.GetManifestResourceStream(resourceName)!;
            assetStream.Length.Should().Be(asset.GetProperty("length").GetInt64());
            Convert.ToHexString(SHA256.HashData(assetStream)).Should().Be(asset.GetProperty("sha256").GetString());
        }
    }
}
