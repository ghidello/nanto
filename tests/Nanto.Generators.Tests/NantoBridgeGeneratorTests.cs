using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nanto.Generators.Tests;

public sealed class NantoBridgeGeneratorTests
{
    public static TheoryData<string, string, string> UnsupportedDtoContracts => new()
    {
        { string.Empty, "object", "object and platform handle types" },
        { string.Empty, "dynamic", "open, pointer, function-pointer, and dynamic types" },
        { string.Empty, "System.IntPtr", "object and platform handle types" },
        { string.Empty, "System.Runtime.InteropServices.GCHandle", "platform handle types" },
        { "public interface Project { int Id { get; } }", "Project", "abstract, interface, inherited, and polymorphic DTOs" },
        { "public abstract class Project { public int Id { get; init; } }", "Project", "abstract, interface, inherited, and polymorphic DTOs" },
        { "public class Entity { } public sealed class Project : Entity { public int Id { get; init; } }", "Project", "abstract, interface, inherited, and polymorphic DTOs" },
        { "[JsonPolymorphic] public class Project { public int Id { get; init; } }", "Project", "custom JSON serialization and polymorphic DTOs" },
        { "public sealed class Project { [JsonPropertyName(\"project_id\")] public int Id { get; init; } }", "Project", "custom JSON serialization" },
        {
            "public sealed class ProjectHandle : SafeHandle { public ProjectHandle() : base(IntPtr.Zero, true) { } public override bool IsInvalid => true; protected override bool ReleaseHandle() => true; }",
            "ProjectHandle",
            "platform handle types"
        },
    };

    [Fact]
    public void GeneratesTypedCapabilitiesAndRegistrationForComposedApi()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Nanto;

            [NantoApi]
            public sealed class ProjectsApi
            {
                [NantoCommand]
                public ValueTask<string> OpenAsync(int projectId, CancellationToken cancellationToken) => ValueTask.FromResult(projectId.ToString());

                [NantoEvent]
                public NantoEvent<string> Changed { get; } = new();
            }

            [NantoApiPart<ProjectsApi>]
            public sealed class ProjectBuilds
            {
                [NantoCommand]
                public Task<int> BuildAsync(int projectId, NantoCommandContext context, CancellationToken cancellationToken) => Task.FromResult(projectId);
            }

            namespace Nanto.Generated
            {
                [global::System.Text.Json.Serialization.JsonSerializable(typeof(int))]
                [global::System.Text.Json.Serialization.JsonSerializable(typeof(string))]
                internal sealed partial class NantoGeneratedJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext;
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().BeEmpty();
        var generated = result.GeneratedTrees.Should().ContainSingle().Subject.ToString();
        generated.Should().Contain("public static class Projects");
        generated.Should().Contain("Open { get; }");
        generated.Should().Contain("Build { get; }");
        generated.Should().Contain("Changed { get; }");
        generated.Should().Contain("projects.open");
        generated.Should().Contain("projects.build");
        generated.Should().Contain("projects.changed");
        generated.Should().Contain("Add(this global::Nanto.NantoBridgeConfiguration bridge, global::ProjectsApi api)");
        generated.Should().Contain("Add(this global::Nanto.NantoBridgeConfiguration bridge, global::ProjectBuilds api)");
    }

    [Fact]
    public void ReportsDuplicateGeneratedCommandName()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<string> Open() => Task.FromResult("");

                [NantoCommand]
                public Task<string> OpenAsync() => Task.FromResult("");
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1001");
    }

    [Fact]
    public void ReportsUnrelatedTypesWithTheSameFrontendGroup()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            namespace First { public sealed class ProjectsApi { [NantoCommand] public Task<int> OpenAsync() => Task.FromResult(1); } }
            namespace Second { public sealed class ProjectsApi { [NantoCommand] public Task<int> CloseAsync() => Task.FromResult(1); } }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1005");
    }

    [Fact]
    public void ReportsSerializedParameterAfterInjectedParameter()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<string> OpenAsync(CancellationToken cancellationToken, int projectId) => Task.FromResult("");
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002");
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("in")]
    [InlineData("out")]
    public void ReportsByReferenceCommandParameters(string modifier)
    {
        var initialization = modifier == "out" ? "projectId = 0; " : string.Empty;
        var source = $$"""
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task OpenAsync({{modifier}} int projectId) { {{initialization}}return Task.CompletedTask; }
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("ref, in, and out", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public static Task OpenAsync() => Task.CompletedTask;", "static methods")]
    [InlineData("public Task OpenAsync<T>() => Task.CompletedTask;", "generic methods")]
    [InlineData("public Task OpenAsync(int projectId = 0) => Task.CompletedTask;", "optional and params-array")]
    [InlineData("public Task OpenAsync(params int[] projectIds) => Task.CompletedTask;", "optional and params-array")]
    [InlineData("public Task OpenAsync(NantoCommandContext first, NantoCommandContext second) => Task.CompletedTask;", "at most one NantoCommandContext")]
    [InlineData("public int Open() => 0;", "return type must be Task")]
    [InlineData("public Task<NantoResult<int, int>> OpenAsync() => Task.FromResult(default(NantoResult<int, int>));", "requires different value and error types")]
    public void ReportsUnsupportedCommandShapes(string declaration, string expectedMessage)
    {
        var source = $$"""
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                {{declaration}}
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsCommandOnGenericContainingType()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi<T>
            {
                [NantoCommand]
                public Task OpenAsync() => Task.CompletedTask;
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("open or containing generic methods", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsNonOrdinaryCommandMethod()
    {
        const string source = """
            using Nanto;

            public sealed class ProjectValue
            {
                [NantoCommand]
                public static ProjectValue operator +(ProjectValue left, ProjectValue right) => left;
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("only ordinary methods", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public unsafe Task OpenAsync(int* value) => Task.CompletedTask;")]
    [InlineData("public unsafe Task OpenAsync(delegate*<void> callback) => Task.CompletedTask;")]
    public void ReportsPointerCommandParameters(string declaration)
    {
        var source = $$"""
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                {{declaration}}
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("open, pointer, function-pointer, and dynamic types", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsCommandThatIsInaccessibleFromGeneratedCode()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                private Task OpenAsync() => Task.CompletedTask;
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("accessible from generated code", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsEventWhoseGetterIsInaccessibleFromGeneratedCode()
    {
        const string source = """
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoEvent]
                public NantoEvent<int> Changed { private get; set; } = new();
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("property, getter", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsStaticEventProperty()
    {
        const string source = """
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoEvent]
                public static NantoEvent<int> Changed { get; } = new();
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("static event properties", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public NantoEvent<int> this[int index] => new();", "indexed event properties")]
    [InlineData("public int Changed { get; }", "must have type NantoEvent<T>")]
    public void ReportsUnsupportedEventShapes(string declaration, string expectedMessage)
    {
        var source = $$"""
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoEvent]
                {{declaration}}
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsCyclicDtoGraph()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Node> LoadAsync() => Task.FromResult(new Node());
            }

            public sealed class Node
            {
                public Node? Parent { get; init; }
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("cyclic object graphs", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsDelegateDtoMember()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Project> LoadAsync() => Task.FromResult(new Project());
            }

            public sealed class Project
            {
                public Action Changed { get; init; } = static () => { };
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("delegates", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("ulong")]
    [InlineData("decimal")]
    public void ReportsNumericTypesWithoutLosslessJavaScriptRepresentation(string type)
    {
        var source = $$"""
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<{{type}}> LoadAsync() => Task.FromResult(default({{type}}));
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("lossless wire representation", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsSystemTypesWithoutDefinedTypeScriptRepresentation()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Version> LoadAsync() => Task.FromResult(new Version(1, 0));
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("TypeScript wire representation", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("byte[]")]
    [InlineData("ReadOnlyMemory<byte>")]
    public void ReportsDeferredBinaryContracts(string type)
    {
        var source = $$"""
            using System;
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<{{type}}> LoadAsync() => Task.FromResult(default({{type}}));
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("binary values are deferred", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsFlagsEnumsWithoutAClosedStringUnion()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Nanto;

            [Flags]
            public enum Permission { Read = 1, Write = 2 }

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Permission> LoadAsync() => Task.FromResult(Permission.Read);
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("flags enums", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsDtoPropertiesWithCollidingWireNames()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            public sealed record Project(string URL, int Url);

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Project> LoadAsync() => Task.FromResult(new Project("url", 1));
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("same camel-case JSON name", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(UnsupportedDtoContracts))]
    public void ReportsUnsupportedDtoContracts(string declaration, string type, string expectedMessage)
    {
        var source = $$"""
            using System;
            using System.Runtime.InteropServices;
            using System.Text.Json.Serialization;
            using System.Threading.Tasks;
            using Nanto;

            {{declaration}}

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<{{type}}> LoadAsync() => Task.FromResult(default({{type}})!);
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1002"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsApiPartWhoseRootIsNotMarkedAsAnApi()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi;

            [NantoApiPart<ProjectsApi>]
            public sealed class ProjectBuilds
            {
                [NantoCommand]
                public Task BuildAsync() => Task.CompletedTask;
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1003");
    }

    [Fact]
    public void ReportsGeneratedCommandIdentifierCollision()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task Command18236Async() => Task.CompletedTask;

                [NantoCommand]
                public Task Command55279Async() => Task.CompletedTask;
            }
            """;

        var result = Run(source);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "NANTO1004");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void SerializedParameterNamesChangeCommandIdsAndManifestEntries()
    {
        const string firstSource = """
            #nullable enable
            using System.Threading.Tasks;
            using Nanto;
            public sealed record Project(string Name);
            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Project> OpenAsync(string projectName) => Task.FromResult(new Project(projectName));
            }
            """;
        const string renamedSource = """
            #nullable enable
            using System.Threading.Tasks;
            using Nanto;
            public sealed record Project(string Name);
            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Project> OpenAsync(string name) => Task.FromResult(new Project(name));
            }
            """;
        var first = Nanto.Sdk.Program.DiscoverFrontendMembers(CreateCompilation(firstSource)).Should().ContainSingle().Subject;
        var renamed = Nanto.Sdk.Program.DiscoverFrontendMembers(CreateCompilation(renamedSource)).Should().ContainSingle().Subject;

        first.CanonicalSignature.Should().NotBe(renamed.CanonicalSignature);
        first.ManifestEntry.Should().NotBe(renamed.ManifestEntry);
        first.Id.Should().NotBe(renamed.Id);
    }

    [Fact]
    public void DtoNullabilityChangesManifestWithoutChangingCommandId()
    {
        const string requiredSource = """
            #nullable enable
            using System.Threading.Tasks;
            using Nanto;
            public sealed record Project(string Name);
            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Project> OpenAsync() => Task.FromResult(new Project("name"));
            }
            """;
        const string nullableSource = """
            #nullable enable
            using System.Threading.Tasks;
            using Nanto;
            public sealed record Project(string? Name);
            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Project> OpenAsync() => Task.FromResult(new Project(null));
            }
            """;
        var required = Nanto.Sdk.Program.DiscoverFrontendMembers(CreateCompilation(requiredSource)).Should().ContainSingle().Subject;
        var nullable = Nanto.Sdk.Program.DiscoverFrontendMembers(CreateCompilation(nullableSource)).Should().ContainSingle().Subject;

        required.CanonicalSignature.Should().Be(nullable.CanonicalSignature);
        required.Id.Should().Be(nullable.Id);
        required.ManifestEntry.Should().NotBe(nullable.ManifestEntry);
    }

    [Fact]
    public void JsonContextRejectsUndefinedEnumValues()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;
            public enum Status { Ready, Complete }
            public sealed record Project(Status Status);
            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<Project> OpenAsync() => Task.FromResult(new Project(Status.Ready));
            }
            """;

        var context = Nanto.Sdk.Program.CreateJsonContext(CreateCompilation(source));

        context.Should().Contain("JsonStringEnumConverter<global::Status>");
        context.Should().Contain("allowIntegerValues: false");
        context.Should().NotContain("UseStringEnumConverter");
    }

    [Fact]
    public void GeneratedCSharpAlwaysUsesLfLineEndings()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;
            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task OpenAsync() => Task.CompletedTask;
            }
            """;

        var result = Run(source);

        result.GeneratedTrees.Should().ContainSingle().Subject.GetText(TestContext.Current.CancellationToken).ToString().Should().NotContain("\r");
    }

    [Fact]
    public void GeneratedCSharpIsStableAcrossSyntaxTreeOrder()
    {
        const string projectsSource = """
            using System.Threading.Tasks;
            using Nanto;

            [NantoApi]
            public sealed partial class ProjectsApi
            {
                [NantoCommand]
                public Task<int> OpenAsync(int projectId) => Task.FromResult(projectId);
            }

            namespace Nanto.Generated
            {
                [global::System.Text.Json.Serialization.JsonSerializable(typeof(int))]
                internal sealed partial class NantoGeneratedJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext;
            }
            """;
        const string buildsSource = """
            using System.Collections.Generic;
            using Nanto;

            [NantoApiPart<ProjectsApi>]
            public sealed class ProjectBuilds
            {
                [NantoCommand]
                public async IAsyncEnumerable<int> BuildAsync(int projectId)
                {
                    yield return projectId;
                    await System.Threading.Tasks.Task.CompletedTask;
                }

                [NantoEvent]
                public NantoEvent<int> Changed { get; } = new();
            }
            """;

        var first = Run(CreateCompilation([projectsSource, buildsSource])).GeneratedTrees.Should().ContainSingle().Subject.ToString();
        var reordered = Run(CreateCompilation([buildsSource, projectsSource])).GeneratedTrees.Should().ContainSingle().Subject.ToString();

        reordered.Should().Be(first);
    }

    [Fact]
    public void UnchangedCompilationReusesIncrementalGeneratorOutput()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<int> OpenAsync(int projectId) => Task.FromResult(projectId);
            }
            """;
        var compilation = CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new NantoBridgeGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        var outputs = driver.GetRunResult().Results.Should().ContainSingle().Subject.TrackedOutputSteps
            .SelectMany(static step => step.Value)
            .SelectMany(static step => step.Outputs)
            .ToArray();
        outputs.Should().NotBeEmpty();
        outputs.Should().OnlyContain(static output => output.Reason == IncrementalStepRunReason.Cached);
    }

    [Fact]
    public void RepresentativeContractMatchesCSharpAndTypeScriptGoldenFingerprints()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Nanto;

            public enum ProjectStatus { Ready, Complete }
            public sealed record Project(int Id, string Name, ProjectStatus Status);
            public sealed record ProjectFailure(string Code);

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public Task<NantoResult<Project, ProjectFailure>> OpenAsync(int projectId) =>
                    Task.FromResult(NantoResult.Success<Project, ProjectFailure>(new Project(projectId, "sample", ProjectStatus.Ready)));

                [NantoCommand]
                public async IAsyncEnumerable<Project> BuildAsync(int projectId)
                {
                    yield return new Project(projectId, "sample", ProjectStatus.Complete);
                    await Task.CompletedTask;
                }

                [NantoEvent]
                public NantoEvent<Project> Changed { get; } = new();
            }

            namespace Nanto.Generated
            {
                [global::System.Text.Json.Serialization.JsonSerializable(typeof(int))]
                [global::System.Text.Json.Serialization.JsonSerializable(typeof(Project))]
                [global::System.Text.Json.Serialization.JsonSerializable(typeof(ProjectFailure))]
                internal sealed partial class NantoGeneratedJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext;
            }
            """;
        var compilation = CreateCompilation(source);
        var generatedCSharp = Run(compilation).GeneratedTrees.Should().ContainSingle().Subject.ToString();
        var generatedTypeScript = Nanto.Sdk.Program.CreateTypeScript(Nanto.Sdk.Program.DiscoverFrontendMembers(compilation), string.Empty);

        Fingerprint(generatedCSharp).Should().Be("4E41B6474A76DB20BEC9C2CF3A5D78C4D63B3DE18E0DC70266AD4506DB90F2EE");
        Fingerprint(generatedTypeScript).Should().Be("183B43F9B098C5D246980AADF616590AC024C6498290076FC831FC602F235577");
    }

    [Fact]
    public void TypeScriptEmitterQualifiesCollidingDtoNames()
    {
        const string source = """
            namespace Sales { public sealed record Project(int Id); }
            namespace Build { public sealed record Project(string Name); }
            """;
        var compilation = CreateCompilation(source);
        var salesProject = compilation.GetTypeByMetadataName("Sales.Project")!;
        var buildProject = compilation.GetTypeByMetadataName("Build.Project")!;
        var emitter = new Nanto.Sdk.Program.TypeScriptEmitter();
        emitter.AddRootType(salesProject);
        emitter.AddRootType(buildProject);

        emitter.PrepareNames();
        var salesName = emitter.GetTypeName(salesProject);
        var buildName = emitter.GetTypeName(buildProject);

        salesName.Should().Be("SalesProject");
        buildName.Should().Be("BuildProject");
        emitter.Declarations.ToString().Should().Contain("export interface SalesProject").And.Contain("export interface BuildProject");
    }

    [Fact]
    public void CSharpAndTypeScriptEmittersConsumeTheSameSemanticMember()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Nanto;

            public sealed class ProjectsApi
            {
                [NantoCommand]
                public ValueTask<int> OpenAsync(int projectId, NantoCommandContext context, CancellationToken cancellationToken) =>
                    ValueTask.FromResult(projectId);
            }

            namespace Nanto.Generated
            {
                [global::System.Text.Json.Serialization.JsonSerializable(typeof(int))]
                internal sealed partial class NantoGeneratedJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext;
            }
            """;
        var compilation = CreateCompilation(source);
        var member = Nanto.Sdk.Program.DiscoverFrontendMembers(compilation).Should().ContainSingle().Subject;

        var generatorResult = Run(source);
        var generated = generatorResult.GeneratedTrees.Should().ContainSingle().Subject.ToString();

        generated.Should().Contain(member.Id.ToString(CultureInfo.InvariantCulture) + "U");
        generated.Should().Contain(member.SymbolicName);
        generated.Should().Contain(member.ManifestEntry);
    }

    [Fact]
    public void TypeScriptEmitterPreservesDocumentationAndProjectRelativeSourceLocations()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nanto;

            /// <summary>Project operations.</summary>
            public sealed class ProjectsApi
            {
                /// <summary>Opens the requested <see cref="Project"/>.</summary>
                /// <param name="projectId">The stable project identifier.</param>
                /// <returns>The matching project.</returns>
                [NantoCommand]
                public Task<Project> OpenAsync(int projectId) => Task.FromResult(new Project(projectId));
            }

            /// <summary>A project returned to the frontend.</summary>
            /// <param name="Id">The stable identifier.</param>
            public sealed record Project(int Id);
            """;
        const string projectDirectory = "D:\\repo";
        var compilation = CreateCompilation(source, "D:\\repo\\Contracts\\ProjectsApi.cs");
        var generated = Nanto.Sdk.Program.CreateTypeScript(Nanto.Sdk.Program.DiscoverFrontendMembers(compilation), projectDirectory);

        generated.Should().Contain("Project operations.");
        generated.Should().Contain("Opens the requested Project.");
        generated.Should().Contain("@param projectId The stable project identifier.");
        generated.Should().Contain("@returns The matching project.");
        generated.Should().Contain("A project returned to the frontend.");
        generated.Should().Contain("@source Contracts/ProjectsApi.cs:");
        generated.Should().NotContain(projectDirectory);
    }

    private static GeneratorDriverRunResult Run(string source)
    {
        return Run(CreateCompilation(source));
    }

    private static GeneratorDriverRunResult Run(CSharpCompilation compilation)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NantoBridgeGenerator())
            .WithUpdatedParseOptions(new CSharpParseOptions(LanguageVersion.Preview));
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        var compilationDiagnostics = compilation.AddSyntaxTrees(result.GeneratedTrees).GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            // The ad-hoc test driver does not run System.Text.Json's generator over Nanto's generated context.
            .Where(static diagnostic => diagnostic.Id is not ("CS0534" or "CS7036" or "CS0117")
                || !diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("NantoGeneratedJsonContext", StringComparison.Ordinal)
                    && !diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("JsonSerializerContext", StringComparison.Ordinal));
        compilationDiagnostics.Should().BeEmpty();
        return result;
    }

    private static CSharpCompilation CreateCompilation(string source, string path = "")
    {
        return CreateCompilation([(source, path)]);
    }

    private static CSharpCompilation CreateCompilation(IReadOnlyList<string> sources)
    {
        return CreateCompilation(sources.Select(static source => (source, string.Empty)));
    }

    private static CSharpCompilation CreateCompilation(IEnumerable<(string Source, string Path)> sources)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(NantoCommandAttribute).Assembly.Location));
        return CSharpCompilation.Create(
            "GeneratorTests",
            [.. sources.Select(static source => CSharpSyntaxTree.ParseText(
                source.Source,
                new CSharpParseOptions(LanguageVersion.Preview, documentationMode: DocumentationMode.Diagnose),
                source.Path))],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
