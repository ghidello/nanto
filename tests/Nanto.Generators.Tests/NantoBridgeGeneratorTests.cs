using System.Collections.Immutable;
using System.Globalization;

using AwesomeAssertions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nanto.Generators.Tests;

public sealed class NantoBridgeGeneratorTests
{
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
        var compilation = CreateCompilation(source);
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
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(NantoCommandAttribute).Assembly.Location));
        return CSharpCompilation.Create(
            "GeneratorTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview, documentationMode: DocumentationMode.Diagnose), path)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}
