using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Nanto.Generators;
using Nanto.Sdk.Plugins;

namespace Nanto.Sdk;

internal static class Program
{
    private static readonly SymbolDisplayFormat _typeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static int Main(string[] args)
    {
        if (args is ["assets", var distributionRoot, var manifestOutputPath, var resourcePrefix])
        {
            return GenerateAssetManifest(distributionRoot, manifestOutputPath, resourcePrefix);
        }

        if (args is ["copy", var sourcePath, var outputPath])
        {
            WriteFileAtomically(outputPath, File.ReadAllText(sourcePath));
            return 0;
        }

        if (args is ["plugins", "verify", var verificationResponsePath, var snapshotPath])
        {
            return VerifyPluginSelection(verificationResponsePath, snapshotPath);
        }

        if (args is ["plugins", var pluginResponsePath, var catalogOutputPath, var snapshotOutputPath])
        {
            return GeneratePluginCatalog(pluginResponsePath, catalogOutputPath, snapshotOutputPath);
        }

        if (args is not [var responsePath, var contextOutputPath, var typeScriptOutputPath])
        {
            Console.Error.WriteLine("Usage: Nanto.Sdk <response-file> <context-output-file> <typescript-output-file>");
            return 2;
        }

        var sources = new List<string>();
        var references = new List<string>();
        var defines = Array.Empty<string>();
        var languageVersion = LanguageVersion.Latest;
        var nullableContext = NullableContextOptions.Disable;
        var projectDirectory = Directory.GetCurrentDirectory();
        foreach (var line in File.ReadLines(responsePath))
        {
            if (line.StartsWith("define=", StringComparison.Ordinal))
            {
                defines = line[7..].Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (line.StartsWith("langversion=", StringComparison.Ordinal)
                && LanguageVersionFacts.TryParse(line[12..], out var parsedLanguageVersion))
            {
                languageVersion = parsedLanguageVersion;
            }
            else if (line.StartsWith("nullable=", StringComparison.Ordinal))
            {
                nullableContext = ParseNullableContext(line[9..]);
            }
            else if (line.StartsWith("projectdir=", StringComparison.Ordinal))
            {
                projectDirectory = line[11..];
            }
            else if (line.StartsWith("source=", StringComparison.Ordinal))
            {
                sources.Add(line[7..]);
            }
            else if (line.StartsWith("reference=", StringComparison.Ordinal))
            {
                references.Add(line[10..]);
            }
        }

        var syntaxTrees = sources
            .Where(File.Exists)
            .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(contextOutputPath), StringComparison.OrdinalIgnoreCase))
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(languageVersion, preprocessorSymbols: defines), path))
            .ToImmutableArray();
        var metadataReferences = references.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Nanto.Sdk.ContractModel",
            syntaxTrees,
            metadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullableContext));
        using var outputLock = AcquireOutputLock(Path.Combine(Path.GetDirectoryName(responsePath)!, ".nanto-contracts.lock"));
        WriteContext(contextOutputPath, CreateJsonContext(compilation));
        WriteTypeScript(typeScriptOutputPath, DiscoverFrontendMembers(compilation), projectDirectory);
        return 0;
    }

    private static int GeneratePluginCatalog(string responsePath, string catalogOutputPath, string snapshotOutputPath)
    {
        try
        {
            PluginSelectionCompilation compilation = CompilePluginSelection(responsePath);
            using var outputLock = AcquireOutputLock(Path.Combine(Path.GetDirectoryName(snapshotOutputPath)!, ".nanto-plugins.lock"));
            WriteFileAtomically(catalogOutputPath, NantoPluginManifestCompiler.Serialize(compilation.Catalog));
            WriteFileAtomically(snapshotOutputPath, NantoPluginSelectionSnapshotCompiler.Serialize(compilation.Snapshot));
            return 0;
        }
        catch (NantoPluginManifestException exception)
        {
            Console.Error.WriteLine(exception);
            return 8;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _ = exception;
            Console.Error.WriteLine("NANTO4114: plugin-catalog($): Plugin catalog inputs could not be processed.");
            return 8;
        }
    }

    private static int VerifyPluginSelection(string responsePath, string snapshotPath)
    {
        try
        {
            PluginSelectionCompilation compilation = CompilePluginSelection(responsePath);
            NantoPluginSelectionSnapshotCompiler.EnsureFresh(snapshotPath, compilation.Snapshot);
            return 0;
        }
        catch (NantoPluginManifestException exception)
        {
            Console.Error.WriteLine(exception);
            return 8;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            _ = exception;
            Console.Error.WriteLine("NANTO4114: plugin-catalog($): Plugin catalog inputs could not be processed.");
            return 8;
        }
    }

    private static PluginSelectionCompilation CompilePluginSelection(string responsePath)
    {
        string? assetsFile = null;
        string? targetFramework = null;
        string? projectFile = null;
        string? configuration = null;
        var references = new List<NantoPluginManifestReference>();
        var restoreProperties = new List<KeyValuePair<string, string>>();
        var restoreInputs = new List<string>();
        foreach (string line in File.ReadLines(responsePath))
        {
            if (line.StartsWith("assetsfile=", StringComparison.Ordinal))
            {
                assetsFile = line[11..];
            }
            else if (line.StartsWith("targetframework=", StringComparison.Ordinal))
            {
                targetFramework = line[16..];
            }
            else if (line.StartsWith("projectfile=", StringComparison.Ordinal))
            {
                projectFile = line[12..];
            }
            else if (line.StartsWith("configuration=", StringComparison.Ordinal))
            {
                configuration = line[14..];
            }
            else if (line.StartsWith("restoreproperty=", StringComparison.Ordinal))
            {
                string value = line[16..];
                int separator = value.IndexOf('|');
                if (separator <= 0)
                {
                    throw new InvalidDataException("Plugin restore-property response entry is malformed.");
                }

                restoreProperties.Add(new KeyValuePair<string, string>(value[..separator], value[(separator + 1)..]));
            }
            else if (line.StartsWith("restoreinput=", StringComparison.Ordinal))
            {
                restoreInputs.Add(line[13..]);
            }
            else if (line.StartsWith("manifest=", StringComparison.Ordinal))
            {
                string value = line[9..];
                int separator = value.LastIndexOf('|');
                if (separator <= 0 || separator == value.Length - 1)
                {
                    throw new InvalidDataException("Plugin manifest response entry is malformed.");
                }

                references.Add(new NantoPluginManifestReference(value[..separator], value[(separator + 1)..]));
            }
        }

        if (assetsFile is null || targetFramework is null || projectFile is null || configuration is null)
        {
            throw new InvalidDataException("Plugin manifest response is missing restore-graph inputs.");
        }

        NantoPluginManifestInput[] inputs = NantoPluginSelectionReader.Read(assetsFile, targetFramework, references);
        NantoPluginCatalogDocument catalog = NantoPluginManifestCompiler.Compile(inputs);
        NantoPluginSelectionSnapshotDocument snapshot = NantoPluginSelectionSnapshotCompiler.Create(
            assetsFile,
            projectFile,
            targetFramework,
            configuration,
            catalog,
            restoreProperties,
            restoreInputs);
        return new PluginSelectionCompilation(catalog, snapshot);
    }

    private static int GenerateAssetManifest(string distributionRoot, string manifestOutputPath, string resourcePrefix)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(distributionRoot));
            if (!Directory.Exists(root))
            {
                throw new InvalidDataException("The frontend distribution directory does not exist.");
            }

            ThrowIfReparsePoint(new DirectoryInfo(root));
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resources = new HashSet<string>(StringComparer.Ordinal);
            var assets = new List<SdkAssetManifestEntry>();
            foreach (string filePath in EnumerateAssetFiles(root))
            {
                var file = new FileInfo(filePath);
                ThrowIfReparsePointTree(root, file);
                string relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                string normalized = relative.Normalize(NormalizationForm.FormC);
                if (!string.Equals(relative, normalized, StringComparison.Ordinal)
                    || normalized.Split('/').Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or "..")
                    || string.Equals(normalized, "nanto-assets.json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The frontend distribution contains an invalid or reserved asset path.");
                }

                string assetPath = "/" + normalized;
                if (!paths.Add(assetPath) || !caseInsensitivePaths.Add(assetPath))
                {
                    throw new InvalidDataException("The frontend distribution contains a duplicate or case-colliding asset path.");
                }

                long initialLength = file.Length;
                DateTime initialWrite = file.LastWriteTimeUtc;
                string hash;
                using (FileStream stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }

                file.Refresh();
                if (file.Length != initialLength || file.LastWriteTimeUtc != initialWrite)
                {
                    throw new InvalidDataException("A frontend asset changed while its manifest was generated.");
                }

                string resourceName = resourcePrefix + hash + "/" + normalized;
                if (!resources.Add(resourceName))
                {
                    throw new InvalidDataException("The frontend distribution produced a duplicate resource identity.");
                }

                assets.Add(new SdkAssetManifestEntry
                {
                    Path = assetPath,
                    ResourceName = resourceName,
                    Length = initialLength,
                    Sha256 = hash,
                });
            }

            assets.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
            if (assets.All(static asset => asset.Path != "/index.html"))
            {
                throw new InvalidDataException("The frontend distribution must contain index.html.");
            }

            string json = JsonSerializer.Serialize(
                new SdkAssetManifestDocument { SchemaVersion = 1, Assets = [.. assets] },
                SdkAssetManifestJsonContext.Default.SdkAssetManifestDocument);
            WriteFileAtomically(manifestOutputPath, json);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"NANTO_ASSETS: {exception.Message}");
            return 7;
        }
    }

    private static IEnumerable<string> EnumerateAssetFiles(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out DirectoryInfo? directory))
        {
            ThrowIfReparsePoint(directory);
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
            {
                ThrowIfReparsePoint(entry);
                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                }
                else if (entry is FileInfo file)
                {
                    yield return file.FullName;
                }
            }
        }
    }

    private static void ThrowIfReparsePointTree(string root, FileInfo file)
    {
        ThrowIfReparsePoint(file);
        for (DirectoryInfo? directory = file.Directory; directory is not null && directory.FullName.Length >= root.Length; directory = directory.Parent)
        {
            ThrowIfReparsePoint(directory);
            if (string.Equals(directory.FullName, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    private static void ThrowIfReparsePoint(FileSystemInfo entry)
    {
        entry.Refresh();
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The frontend distribution must not contain reparse points or symbolic links.");
        }
    }

    private static NullableContextOptions ParseNullableContext(string value) => value.ToLowerInvariant() switch
    {
        "enable" => NullableContextOptions.Enable,
        "warnings" => NullableContextOptions.Warnings,
        "annotations" => NullableContextOptions.Annotations,
        _ => NullableContextOptions.Disable,
    };

    private static (SortedSet<string> Types, SortedSet<string> EnumTypes) DiscoverTypes(CSharpCompilation compilation)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
        var enumTypes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var methodDeclaration in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(methodDeclaration) is not IMethodSymbol method || !HasAttribute(method, "Nanto.NantoCommandAttribute"))
                {
                    continue;
                }

                foreach (var type in NantoContractTypes.GetSerializationTypes(method))
                {
                    AddType(type, types, enumTypes);
                }
            }

            foreach (var propertyDeclaration in tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(propertyDeclaration) is not IPropertySymbol property
                    || !HasAttribute(property, "Nanto.NantoEventAttribute")
                    || property.Type is not INamedTypeSymbol { TypeArguments.Length: 1 })
                {
                    continue;
                }

                foreach (var type in NantoContractTypes.GetSerializationTypes(property))
                {
                    AddType(type, types, enumTypes);
                }
            }
        }

        if (types.Count == 0)
        {
            types.Add("global::System.Boolean");
        }

        return (types, enumTypes);
    }

    internal static NantoContractMember[] DiscoverFrontendMembers(CSharpCompilation compilation)
    {
        var members = new List<NantoContractMember>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var methodDeclaration in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(methodDeclaration) is not IMethodSymbol method || !HasAttribute(method, "Nanto.NantoCommandAttribute"))
                {
                    continue;
                }

                if (NantoContractTypes.TryCreateMember(method.ContainingType, method, out var member, out _))
                {
                    members.Add(member!);
                }
            }

            foreach (var propertyDeclaration in tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(propertyDeclaration) is not IPropertySymbol property
                    || !HasAttribute(property, "Nanto.NantoEventAttribute")
                    || property.Type is not INamedTypeSymbol { TypeArguments.Length: 1 } eventType)
                {
                    continue;
                }

                if (NantoContractTypes.TryCreateMember(property.ContainingType, property, out var member, out _))
                {
                    members.Add(member!);
                }
            }
        }

        return members.OrderBy(static member => member.GroupName, StringComparer.Ordinal)
            .ThenBy(static member => member.MemberName, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddType(ITypeSymbol type, ISet<string> types, ISet<string> enumTypes)
    {
        if (type is INamedTypeSymbol { Name: "NantoResult", TypeArguments.Length: 2 } result
            && result.ContainingNamespace.ToDisplayString() == "Nanto")
        {
            AddType(result.TypeArguments[0], types, enumTypes);
            AddType(result.TypeArguments[1], types, enumTypes);
            return;
        }

        types.Add(type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(_typeFormat));
        CollectEnumTypes(type, enumTypes, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
    }

    private static void CollectEnumTypes(ITypeSymbol type, ISet<string> enumTypes, ISet<ITypeSymbol> visited)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.None);
        if (!visited.Add(type))
        {
            return;
        }

        if (type is IArrayTypeSymbol array)
        {
            CollectEnumTypes(array.ElementType, enumTypes, visited);
            return;
        }

        if (type is not INamedTypeSymbol named)
        {
            return;
        }

        if (named.TypeKind == TypeKind.Enum)
        {
            enumTypes.Add(named.ToDisplayString(_typeFormat));
            return;
        }

        foreach (var argument in named.TypeArguments)
        {
            CollectEnumTypes(argument, enumTypes, visited);
        }

        if (named.ContainingNamespace.ToDisplayString().StartsWith("System", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var property in named.GetMembers().OfType<IPropertySymbol>()
            .Where(static property => !property.IsStatic && property.GetMethod is { DeclaredAccessibility: Accessibility.Public }))
        {
            CollectEnumTypes(property.Type, enumTypes, visited);
        }
    }

    private static bool HasAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static string ToPascalCase(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    internal static string CreateJsonContext(CSharpCompilation compilation)
    {
        var (types, enumTypes) = DiscoverTypes(compilation);
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace Nanto.Generated;");
        builder.AppendLine();
        builder.AppendLine("[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(");
        if (enumTypes.Count > 0)
        {
            builder.Append("    Converters = new global::System.Type[] { ");
            for (var index = 0; index < enumTypes.Count; index++)
            {
                builder.Append("typeof(NantoGeneratedEnumConverter").Append(index).Append("), ");
            }

            builder.AppendLine("},");
        }

        builder.AppendLine("    PropertyNamingPolicy = global::System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,");
        builder.AppendLine("    RespectNullableAnnotations = true,");
        builder.AppendLine("    RespectRequiredConstructorParameters = true)]");
        foreach (var type in types)
        {
            builder.Append("[global::System.Text.Json.Serialization.JsonSerializable(typeof(").Append(type).AppendLine("))]");
        }

        builder.AppendLine("internal sealed partial class NantoGeneratedJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext;");
        var enumIndex = 0;
        foreach (var enumType in enumTypes)
        {
            builder.AppendLine();
            builder.Append("internal sealed class NantoGeneratedEnumConverter").Append(enumIndex)
                .Append(" : global::System.Text.Json.Serialization.JsonStringEnumConverter<").Append(enumType).AppendLine(">");
            builder.AppendLine("{");
            builder.Append("    public NantoGeneratedEnumConverter").Append(enumIndex)
                .AppendLine("() : base(namingPolicy: null, allowIntegerValues: false)");
            builder.AppendLine("    {");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            enumIndex++;
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void WriteContext(string outputPath, string source)
    {
        WriteFileAtomically(outputPath, source);
    }

    private static void WriteTypeScript(string outputPath, IReadOnlyList<NantoContractMember> members, string projectDirectory)
    {
        var source = CreateTypeScript(members, projectDirectory);
        WriteFileAtomically(outputPath, source);
    }

    internal static void WriteFileAtomically(string outputPath, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(source);
        string directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = outputPath + ".nanto.tmp";
        File.Delete(temporaryPath);
        byte[] contents = new UTF8Encoding(false).GetBytes(source);
        if (File.Exists(outputPath) && File.ReadAllBytes(outputPath).AsSpan().SequenceEqual(contents))
        {
            return;
        }

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static FileStream AcquireOutputLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    internal static string CreateTypeScript(IReadOnlyList<NantoContractMember> members, string projectDirectory)
    {
        var emitter = new TypeScriptEmitter(projectDirectory);
        foreach (var member in members)
        {
            foreach (var parameter in member.Parameters)
            {
                emitter.AddRootType(parameter.Type);
            }

            if (member.PayloadType is not null)
            {
                emitter.AddRootType(member.PayloadType);
            }
        }

        emitter.PrepareNames();
        foreach (var member in members)
        {
            foreach (var parameter in member.Parameters)
            {
                _ = emitter.GetTypeName(parameter.Type);
            }

            if (member.PayloadType is not null)
            {
                _ = emitter.GetTypeName(member.PayloadType);
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("import { NantoClient } from \"@nanto/core\";");
        builder.AppendLine();
        builder.Append("export const manifest = \"").Append(NantoContractTypes.ComputeManifestFingerprint(
            members.Select(static member => member.ManifestEntry),
            1)).AppendLine("\" as const;");
        builder.AppendLine("export type NantoResult<T, TError> = { ok: true; value: T } | { ok: false; error: TError };");
        if (emitter.Declarations.Length > 0)
        {
            builder.AppendLine(emitter.Declarations.ToString().TrimEnd());
        }

        builder.AppendLine();
        builder.AppendLine("export function createApp(client: NantoClient) {");
        builder.AppendLine("  return {");
        foreach (var group in members.GroupBy(static member => member.GroupName).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            AppendDocumentation(builder, group.First().GroupType, "    ", projectDirectory);
            builder.Append("    ").Append(group.Key).AppendLine(": {");
            foreach (var member in group.OrderBy(static member => member.MemberName, StringComparer.Ordinal))
            {
                AppendDocumentation(builder, member.Symbol, "      ", projectDirectory);
                var payloadType = member.PayloadType is null ? "void" : emitter.GetTypeName(member.PayloadType);
                if (member.Kind == NantoContractMemberKind.Event)
                {
                    builder.Append("      ").Append(member.MemberName).AppendLine(": {");
                    builder.Append("        subscribe: (options?: { signal?: AbortSignal }) => client.subscribe<")
                        .Append(payloadType).Append(">(").Append(member.Id).AppendLine(", options?.signal),");
                    builder.AppendLine("      },");
                    continue;
                }

                builder.Append("      ").Append(member.MemberName).Append(": (");
                foreach (var parameter in member.Parameters)
                {
                    builder.Append(GetTypeScriptIdentifier(parameter.Name)).Append(": ").Append(emitter.GetTypeName(parameter.Type)).Append(", ");
                }

                builder.Append("$options?: { signal?: AbortSignal }) => client.")
                    .Append(member.Kind == NantoContractMemberKind.Stream ? "stream" : "invoke").Append('<').Append(payloadType).Append(">(")
                    .Append(member.Id).Append(", { ").Append(string.Join(", ", member.Parameters.Select(static parameter =>
                        parameter.Name == GetTypeScriptIdentifier(parameter.Name)
                            ? parameter.Name
                            : parameter.Name + ": " + GetTypeScriptIdentifier(parameter.Name))))
                    .AppendLine(" }, $options?.signal),");
            }

            builder.AppendLine("    },");
        }

        builder.AppendLine("  } as const;");
        builder.AppendLine("}");
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void AppendDocumentation(StringBuilder builder, ISymbol symbol, string indent, string projectDirectory)
    {
        var entries = new List<string>();
        var xml = symbol.GetDocumentationCommentXml(preferredCulture: CultureInfo.InvariantCulture, expandIncludes: true);
        if (!string.IsNullOrWhiteSpace(xml))
        {
            try
            {
                var member = XElement.Parse(xml);
                AddDocumentationEntry(entries, null, member.Element("summary"));
                AddDocumentationEntry(entries, "Remarks: ", member.Element("remarks"));
                foreach (var parameter in member.Elements("param"))
                {
                    AddDocumentationEntry(entries, "@param " + parameter.Attribute("name")?.Value + " ", parameter);
                }

                AddDocumentationEntry(entries, "@returns ", member.Element("returns"));
            }
            catch (System.Xml.XmlException)
            {
                // Invalid XML documentation is already reported by the C# compiler; generation remains deterministic.
            }
        }

        if (GetProjectRelativeSource(symbol, projectDirectory) is { } source)
        {
            entries.Add("@source " + source);
        }

        if (entries.Count == 0)
        {
            return;
        }

        builder.Append(indent).AppendLine("/**");
        foreach (var entry in entries)
        {
            builder.Append(indent).Append(" * ").AppendLine(entry.Replace("*/", "* /", StringComparison.Ordinal));
        }

        builder.Append(indent).AppendLine(" */");
    }

    private static void AddDocumentationEntry(List<string> entries, string? prefix, XElement? element)
    {
        if (element is null)
        {
            return;
        }

        var text = NormalizeDocumentationText(RenderDocumentation(element));
        if (text.Length > 0)
        {
            entries.Add((prefix ?? string.Empty) + text);
        }
    }

    private static string RenderDocumentation(XContainer container)
    {
        var builder = new StringBuilder();
        foreach (var node in container.Nodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement { Name.LocalName: "see" or "seealso" } reference:
                    var target = reference.Attribute("cref")?.Value ?? reference.Attribute("href")?.Value;
                    builder.Append(target is { Length: > 2 } && target[1] == ':' ? target[2..] : target);
                    break;
                case XElement { Name.LocalName: "paramref" or "typeparamref" } parameterReference:
                    builder.Append(parameterReference.Attribute("name")?.Value);
                    break;
                case XElement { Name.LocalName: "c" } code:
                    builder.Append('`').Append(code.Value).Append('`');
                    break;
                case XElement element:
                    builder.Append(RenderDocumentation(element));
                    break;
            }
        }

        return builder.ToString();
    }

    private static string NormalizeDocumentationText(string value) => string.Join(
        " ",
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? GetProjectRelativeSource(ISymbol symbol, string projectDirectory)
    {
        var location = symbol.Locations.FirstOrDefault(static location => location.IsInSource);
        if (location?.SourceTree?.FilePath is not { Length: > 0 } sourcePath || string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(Path.GetFullPath(projectDirectory), Path.GetFullPath(sourcePath));
        if (Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        var line = location.GetLineSpan().StartLinePosition.Line + 1;
        return relativePath.Replace('\\', '/') + ":" + line.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record PluginSelectionCompilation(NantoPluginCatalogDocument Catalog, NantoPluginSelectionSnapshotDocument Snapshot);

    internal sealed class TypeScriptEmitter
    {
        private readonly HashSet<ITypeSymbol> _declared = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, string> _names = new(SymbolEqualityComparer.Default);
        private readonly HashSet<ITypeSymbol> _types = new(SymbolEqualityComparer.Default);
        private readonly string _projectDirectory;

        internal StringBuilder Declarations { get; } = new();

        internal TypeScriptEmitter(string? projectDirectory = null)
        {
            _projectDirectory = projectDirectory ?? string.Empty;
        }

        internal void AddRootType(ITypeSymbol type) => DiscoverType(type.WithNullableAnnotation(NullableAnnotation.None));

        internal void PrepareNames()
        {
            var values = _types.OfType<INamedTypeSymbol>()
                .OrderBy(static type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
                .ToArray();
            var simpleNameCounts = values.GroupBy(static type => type.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
            var proposals = values.Select(type => (
                Type: type,
                Name: simpleNameCounts[type.Name] == 1 && !IsReservedTypeScriptName(type.Name)
                    ? type.Name
                    : CreateQualifiedName(type))).ToArray();
            var duplicateProposals = proposals.GroupBy(static proposal => proposal.Name, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var proposal in proposals)
            {
                var name = proposal.Name;
                if (duplicateProposals.Contains(name) || IsReservedTypeScriptName(name))
                {
                    name += "_" + NantoContractTypes.ComputeId(
                        proposal.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToString("x8", CultureInfo.InvariantCulture);
                }

                while (!usedNames.Add(name))
                {
                    name += "_";
                }

                _names.Add(proposal.Type, name);
            }
        }

        internal string GetTypeName(ITypeSymbol type)
        {
            var nullable = type.NullableAnnotation == NullableAnnotation.Annotated;
            var result = GetNonNullableTypeName(type.WithNullableAnnotation(NullableAnnotation.None));
            return nullable && result != "unknown" ? result + " | null" : result;
        }

        private string GetNonNullableTypeName(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
            {
                if (array.ElementType.SpecialType == SpecialType.System_Byte)
                {
                    return "string";
                }

                return "ReadonlyArray<" + GetTypeName(array.ElementType) + ">";
            }

            if (type is not INamedTypeSymbol named)
            {
                return "unknown";
            }

            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                return GetTypeName(named.TypeArguments[0]) + " | null";
            }

            if (named.Name == "NantoResult" && named.TypeArguments.Length == 2 && named.ContainingNamespace.ToDisplayString() == "Nanto")
            {
                return "NantoResult<" + GetTypeName(named.TypeArguments[0]) + ", " + GetTypeName(named.TypeArguments[1]) + ">";
            }

            if (named.SpecialType == SpecialType.System_Boolean)
            {
                return "boolean";
            }

            if (named.SpecialType is SpecialType.System_String or SpecialType.System_Char)
            {
                return "string";
            }

            if (named.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Single or SpecialType.System_Double)
            {
                return "number";
            }

            var namespaceName = named.ContainingNamespace.ToDisplayString();
            if (namespaceName == "System" && named.Name is "Guid" or "DateTime" or "DateTimeOffset" or "DateOnly" or "TimeOnly" or "TimeSpan" or "Uri")
            {
                return "string";
            }

            if (namespaceName == "System.Collections.Generic" && named.TypeArguments.Length > 0)
            {
                if (named.Name.Contains("Dictionary", StringComparison.Ordinal) && named.TypeArguments.Length == 2)
                {
                    return "Readonly<Record<string, " + GetTypeName(named.TypeArguments[1]) + ">>";
                }

                return "ReadonlyArray<" + GetTypeName(named.TypeArguments[^1]) + ">";
            }

            if (named.TypeKind == TypeKind.Enum)
            {
                EmitEnum(named);
                return _names[named];
            }

            if (namespaceName.StartsWith("System", StringComparison.Ordinal))
            {
                return "unknown";
            }

            EmitDto(named);
            return _names[named];
        }

        private void DiscoverType(ITypeSymbol type)
        {
            type = type.WithNullableAnnotation(NullableAnnotation.None);
            if (type is IArrayTypeSymbol array)
            {
                DiscoverType(array.ElementType);
                return;
            }

            if (type is not INamedTypeSymbol named)
            {
                return;
            }

            foreach (var argument in named.TypeArguments)
            {
                DiscoverType(argument);
            }

            var namespaceName = named.ContainingNamespace.ToDisplayString();
            if (named.TypeKind != TypeKind.Enum && namespaceName.StartsWith("System", StringComparison.Ordinal)
                || named.Name == "NantoResult" && namespaceName == "Nanto")
            {
                return;
            }

            if (!_types.Add(named) || named.TypeKind == TypeKind.Enum)
            {
                return;
            }

            foreach (var property in named.GetMembers().OfType<IPropertySymbol>()
                .Where(static property => !property.IsStatic && property.GetMethod is { DeclaredAccessibility: Accessibility.Public }))
            {
                DiscoverType(property.Type);
            }
        }

        private static string CreateQualifiedName(INamedTypeSymbol type)
        {
            var builder = new StringBuilder();
            foreach (var segment in type.ContainingNamespace.ToDisplayString().Split('.'))
            {
                builder.Append(ToPascalCase(segment));
            }

            var containingTypes = new Stack<string>();
            for (var containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
            {
                containingTypes.Push(containing.Name);
            }

            foreach (var containing in containingTypes)
            {
                builder.Append(containing);
            }

            builder.Append(type.Name);
            foreach (var argument in type.TypeArguments)
            {
                builder.Append(argument.Name);
            }

            return builder.ToString();
        }

        private static bool IsReservedTypeScriptName(string name) => name is "NantoClient" or "NantoResult" or "createApp" or "manifest"
            || GetTypeScriptIdentifier(name) != name;

        private void EmitEnum(INamedTypeSymbol type)
        {
            if (!_declared.Add(type))
            {
                return;
            }

            var name = _names[type];
            AppendDocumentation(Declarations, type, string.Empty, _projectDirectory);
            Declarations.Append("export const ").Append(name).AppendLine(" = {");
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue)
                .OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                AppendDocumentation(Declarations, field, "  ", _projectDirectory);
                Declarations.Append("  ").Append(field.Name).Append(": \"").Append(field.Name).AppendLine("\",");
            }

            Declarations.AppendLine("} as const;");
            Declarations.Append("export type ").Append(name).Append(" = typeof ").Append(name)
                .Append("[keyof typeof ").Append(name).AppendLine("];\n");
        }

        private void EmitDto(INamedTypeSymbol type)
        {
            if (!_declared.Add(type))
            {
                return;
            }

            var properties = type.GetMembers().OfType<IPropertySymbol>()
                .Where(static property => !property.IsStatic && property.GetMethod is { DeclaredAccessibility: Accessibility.Public })
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .ToArray();
            var propertyTypes = properties.Select(property => (Property: property, TypeName: GetTypeName(property.Type))).ToArray();
            AppendDocumentation(Declarations, type, string.Empty, _projectDirectory);
            Declarations.Append("export interface ").Append(_names[type]).AppendLine(" {");
            foreach (var property in propertyTypes)
            {
                AppendDocumentation(Declarations, property.Property, "  ", _projectDirectory);
                Declarations.Append("  ").Append(NantoContractTypes.ToCamelCase(property.Property.Name)).Append(": ").Append(property.TypeName).AppendLine(";");
            }

            Declarations.AppendLine("}\n");
        }
    }

    private static string GetTypeScriptIdentifier(string name) => name switch
    {
        "await" or "break" or "case" or "catch" or "class" or "const" or "continue" or "debugger" or "default"
            or "delete" or "do" or "else" or "enum" or "export" or "extends" or "false" or "finally" or "for"
            or "function" or "if" or "implements" or "import" or "in" or "instanceof" or "interface" or "let"
            or "new" or "null" or "package" or "private" or "protected" or "public" or "return" or "static"
            or "super" or "switch" or "this" or "throw" or "true" or "try" or "typeof" or "var" or "void"
            or "while" or "with" or "yield" => "$" + name,
        _ => name,
    };

}