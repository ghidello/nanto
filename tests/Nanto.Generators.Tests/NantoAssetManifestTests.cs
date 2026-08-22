using System.Security.Cryptography;
using System.Text.Json;

using AwesomeAssertions;

namespace Nanto.Generators.Tests;

public sealed class NantoAssetManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nanto-asset-sdk-tests", Guid.NewGuid().ToString("N"));

    public NantoAssetManifestTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AssetsCommandWritesStableSortedManifestAndPreservesUnchangedTimestamp()
    {
        string distribution = Path.Combine(_root, "dist");
        Write(Path.Combine(distribution, "index.html"), "<html></html>");
        Write(Path.Combine(distribution, "assets", "app.js"), "console.log('nanto');");
        string manifest = Path.Combine(_root, "obj", "nanto-assets.json");

        int firstExitCode = Nanto.Sdk.Program.Main(["assets", distribution, manifest, "Nanto.AppAssets/"]);
        DateTime firstWrite = File.GetLastWriteTimeUtc(manifest);
        Thread.Sleep(20);
        int secondExitCode = Nanto.Sdk.Program.Main(["assets", distribution, manifest, "Nanto.AppAssets/"]);

        firstExitCode.Should().Be(0);
        secondExitCode.Should().Be(0);
        File.GetLastWriteTimeUtc(manifest).Should().Be(firstWrite);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        JsonElement.ArrayEnumerator assets = document.RootElement.GetProperty("assets").EnumerateArray();
        JsonElement[] entries = assets.ToArray();
        entries.Select(static entry => entry.GetProperty("path").GetString()).Should().Equal("/assets/app.js", "/index.html");
        foreach (JsonElement entry in entries)
        {
            string hash = entry.GetProperty("sha256").GetString()!;
            hash.Should().MatchRegex("^[0-9A-F]{64}$");
            entry.GetProperty("resourceName").GetString().Should().StartWith("Nanto.AppAssets/" + hash + "/");
        }
    }

    [Fact]
    public void AssetsCommandRejectsMissingIndexAndReservedManifestPath()
    {
        string distribution = Path.Combine(_root, "dist");
        Write(Path.Combine(distribution, "app.js"), "content");
        string manifest = Path.Combine(_root, "obj", "manifest.json");

        Nanto.Sdk.Program.Main(["assets", distribution, manifest, "Nanto.AppAssets/"]).Should().Be(7);

        Write(Path.Combine(distribution, "index.html"), "index");
        Write(Path.Combine(distribution, "nanto-assets.json"), "reserved");
        Nanto.Sdk.Program.Main(["assets", distribution, manifest, "Nanto.AppAssets/"]).Should().Be(7);
        File.Exists(manifest).Should().BeFalse();
    }

    [Fact]
    public void AssetsCommandRecordsExactLengthAndHash()
    {
        byte[] contents = "asset-content"u8.ToArray();
        string distribution = Path.Combine(_root, "dist");
        Directory.CreateDirectory(distribution);
        File.WriteAllBytes(Path.Combine(distribution, "index.html"), contents);
        string manifest = Path.Combine(_root, "obj", "manifest.json");

        Nanto.Sdk.Program.Main(["assets", distribution, manifest, "Nanto.AppAssets/"]).Should().Be(0);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        JsonElement entry = document.RootElement.GetProperty("assets")[0];
        entry.GetProperty("length").GetInt64().Should().Be(contents.Length);
        entry.GetProperty("sha256").GetString().Should().Be(Convert.ToHexString(SHA256.HashData(contents)));
    }

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
