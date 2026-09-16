using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Nanto.Sdk.Capabilities;

namespace Nanto.Generators.Tests;

public sealed class NantoCapabilityCompilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nanto-capability-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CompileNormalizesOriginsValidatesScopesAndProducesDeterministicRedactedOutput()
    {
        NantoCapabilityPermissionCatalogEntry[] catalog = CreateCatalog();
        NantoCapabilityInput local = WriteCapability(
            "local.nanto-capability.json",
            "local-window",
            "[\"local\"]",
            "[\"app:projects.open\"]");
        NantoCapabilityInput remote = WriteCapability(
            "remote.nanto-capability.json",
            "remote-window",
            "[\"https://BÜCHER.example:443/\"]",
            "[{\"identifier\":\"fixture.filesystem:read\",\"scope\":{\"root\":\"$APPDATA/projects\"}}]");

        NantoCompiledCapabilityPolicy first = NantoCapabilityCompiler.Compile([remote, local], catalog);
        NantoCompiledCapabilityPolicy second = NantoCapabilityCompiler.Compile([local, remote], catalog);
        string inspection = NantoCapabilityCompiler.SerializeInspection(first);

        first.Fingerprint.Should().Be(second.Fingerprint);
        NantoCapabilityCompiler.EmitSource(first).Should().Be(NantoCapabilityCompiler.EmitSource(second));
        first.Entries.Select(static entry => entry.Origin).Should().Equal("https://xn--bcher-kva.example", "local");
        first.Entries[0].ScopeJson.Should().Be("{\"root\":\"$APPDATA/projects\"}");
        inspection.Should().Contain(first.Entries[0].ScopeSha256!);
        inspection.Should().NotContain("$APPDATA");
    }

    [Theory]
    [InlineData("\"extra\":true,", "[\"local\"]", "[\"app:projects.open\"]", "NANTO4003")]
    [InlineData("", "[\"http://example.com\"]", "[\"app:projects.open\"]", "NANTO4005")]
    [InlineData("", "[\"local\"]", "[\"app:unknown\"]", "NANTO4006")]
    [InlineData("", "[\"local\"]", "[{\"identifier\":\"app:projects.open\",\"scope\":{}}]", "NANTO4008")]
    [InlineData("", "[\"local\"]", "[\"fixture.filesystem:read\"]", "NANTO4008")]
    [InlineData("", "[\"local\"]", "[{\"identifier\":\"fixture.filesystem:read\",\"scope\":{}}]", "NANTO4009")]
    public void CompileRejectsMalformedSemanticClasses(string extraProperty, string origins, string permissions, string expectedCode)
    {
        NantoCapabilityInput input = WriteCapability("invalid.nanto-capability.json", "invalid", origins, permissions, extraProperty);

        var action = () => NantoCapabilityCompiler.Compile([input], CreateCatalog());

        action.Should().Throw<NantoCapabilityException>().Which.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void CompileRejectsDuplicateGrantAcrossDocuments()
    {
        NantoCapabilityInput first = WriteCapability("first.nanto-capability.json", "first", "[\"local\"]", "[\"app:projects.open\"]");
        NantoCapabilityInput second = WriteCapability("second.nanto-capability.json", "second", "[\"local\"]", "[\"app:projects.open\"]");

        var action = () => NantoCapabilityCompiler.Compile([first, second], CreateCatalog());

        action.Should().Throw<NantoCapabilityException>().Which.Code.Should().Be("NANTO4007");
    }

    [Fact]
    public void CompileRejectsDuplicateDocumentIdentifier()
    {
        NantoCapabilityInput first = WriteCapability("first.nanto-capability.json", "duplicate", "[\"local\"]", "[\"app:projects.open\"]");
        NantoCapabilityInput second = WriteCapability(
            "second.nanto-capability.json",
            "duplicate",
            "[\"https://example.com\"]",
            "[\"app:projects.open\"]");

        var action = () => NantoCapabilityCompiler.Compile([first, second], CreateCatalog());

        action.Should().Throw<NantoCapabilityException>().Which.Code.Should().Be("NANTO4004");
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user@example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?query=true")]
    [InlineData("https://*.example.com")]
    public void CompileRejectsNonExactOrInsecureOrigin(string origin)
    {
        string origins = JsonSerializer.Serialize(new[] { origin });
        NantoCapabilityInput input = WriteCapability("origin.nanto-capability.json", "origin", origins, "[\"app:projects.open\"]");

        var action = () => NantoCapabilityCompiler.Compile([input], CreateCatalog());

        action.Should().Throw<NantoCapabilityException>().Which.Code.Should().Be("NANTO4005");
    }

    [Fact]
    public void CompileRejectsOriginDuplicatedAfterNormalization()
    {
        NantoCapabilityInput input = WriteCapability(
            "origin.nanto-capability.json",
            "origin",
            "[\"https://EXAMPLE.com\",\"https://example.com:443/\"]",
            "[\"app:projects.open\"]");

        var action = () => NantoCapabilityCompiler.Compile([input], CreateCatalog());

        action.Should().Throw<NantoCapabilityException>().Which.Code.Should().Be("NANTO4003");
    }

    [Theory]
    [InlineData("", "NANTO4000")]
    [InlineData("{", "NANTO4002")]
    public void CompileRejectsEmptyOrMalformedDocument(string content, string expectedCode)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "invalid.nanto-capability.json");
        File.WriteAllText(path, content);

        var action = () => NantoCapabilityCompiler.Compile([new NantoCapabilityInput(path, "Capabilities/invalid.json")], CreateCatalog());

        action.Should().Throw<NantoCapabilityException>().Which.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void CompileRejectsInvalidUtf8Document()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "invalid.nanto-capability.json");
        File.WriteAllBytes(path, [0xFF]);

        var action = () => NantoCapabilityCompiler.Compile([new NantoCapabilityInput(path, "Capabilities/invalid.json")], CreateCatalog());

        action.Should().Throw<NantoCapabilityException>().Which.Code.Should().Be("NANTO4001");
    }

    [Fact]
    public void CompileRejectsChangedScopeSchemaHash()
    {
        NantoCapabilityPermissionCatalogEntry[] catalog = CreateCatalog();
        File.AppendAllText(catalog[1].ScopeSchemaPath!, " ");
        NantoCapabilityInput input = WriteCapability(
            "scope.nanto-capability.json",
            "scope",
            "[\"local\"]",
            "[{\"identifier\":\"fixture.filesystem:read\",\"scope\":{\"root\":\"value\"}}]");

        var action = () => NantoCapabilityCompiler.Compile([input], catalog);

        action.Should().Throw<NantoCapabilityException>().Which.Code.Should().Be("NANTO4009");
    }

    [Fact]
    public void CompilePreservesDistinctLargeIntegerScopes()
    {
        string schemaPath = Path.Combine(_root, "integer-v1.schema.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(schemaPath, "{\"type\":\"integer\"}");
        NantoCapabilityPermissionCatalogEntry[] catalog =
        [
            new(
                "fixture.counter:read",
                ["fixtureCounter.read"],
                [],
                schemaPath,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schemaPath)))),
        ];
        NantoCapabilityInput first = WriteCapability(
            "first.nanto-capability.json",
            "first",
            "[\"local\"]",
            "[{\"identifier\":\"fixture.counter:read\",\"scope\":9007199254740992}]");
        NantoCapabilityInput second = WriteCapability(
            "second.nanto-capability.json",
            "second",
            "[\"local\"]",
            "[{\"identifier\":\"fixture.counter:read\",\"scope\":9007199254740993}]");

        NantoCompiledCapabilityPolicy firstPolicy = NantoCapabilityCompiler.Compile([first], catalog);
        NantoCompiledCapabilityPolicy secondPolicy = NantoCapabilityCompiler.Compile([second], catalog);

        firstPolicy.Fingerprint.Should().NotBe(secondPolicy.Fingerprint);
        firstPolicy.Entries[0].ScopeJson.Should().Be("9007199254740992");
        secondPolicy.Entries[0].ScopeJson.Should().Be("9007199254740993");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private NantoCapabilityPermissionCatalogEntry[] CreateCatalog()
    {
        string schemaPath = Path.Combine(_root, "read-v1.schema.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            schemaPath,
            """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["root"],
              "properties": {
                "root": { "type": "string", "minLength": 1, "maxLength": 256 }
              }
            }
            """);
        string schemaHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schemaPath)));
        return
        [
            new NantoCapabilityPermissionCatalogEntry("app:projects.open", ["projects.open"], [42], null, null),
            new NantoCapabilityPermissionCatalogEntry(
                "fixture.filesystem:read",
                ["fixtureFilesystem.read"],
                [],
                schemaPath,
                schemaHash),
        ];
    }

    private NantoCapabilityInput WriteCapability(
        string name,
        string identifier,
        string origins,
        string permissions,
        string extraProperty = "")
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllText(
            path,
            $$"""
            {
              "$schema": "https://nanto.dev/schemas/capability/v1.json",
              "schemaVersion": 1,
              "identifier": "{{identifier}}",
              {{extraProperty}}
              "windows": ["main"],
              "origins": {{origins}},
              "permissions": {{permissions}}
            }
            """);
        return new NantoCapabilityInput(path, "Capabilities/" + name);
    }
}
