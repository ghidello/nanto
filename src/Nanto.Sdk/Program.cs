using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Nanto.Generators;

namespace Nanto.Sdk;

internal static class Program
{
    private static readonly SymbolDisplayFormat _typeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static int Main(string[] args)
    {
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
        var types = DiscoverTypes(compilation);
        WriteContext(contextOutputPath, types);
        WriteTypeScript(typeScriptOutputPath, DiscoverFrontendMembers(compilation));
        return 0;
    }

    private static NullableContextOptions ParseNullableContext(string value) => value.ToLowerInvariant() switch
    {
        "enable" => NullableContextOptions.Enable,
        "warnings" => NullableContextOptions.Warnings,
        "annotations" => NullableContextOptions.Annotations,
        _ => NullableContextOptions.Disable,
    };

    private static SortedSet<string> DiscoverTypes(CSharpCompilation compilation)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
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
                    AddType(type, types);
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
                    AddType(type, types);
                }
            }
        }

        if (types.Count == 0)
        {
            types.Add("global::System.Boolean");
        }

        return types;
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

    private static void AddType(ITypeSymbol type, ISet<string> types)
    {
        if (type is INamedTypeSymbol { Name: "NantoResult", TypeArguments.Length: 2 } result
            && result.ContainingNamespace.ToDisplayString() == "Nanto")
        {
            AddType(result.TypeArguments[0], types);
            AddType(result.TypeArguments[1], types);
            return;
        }

        types.Add(type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(_typeFormat));
    }

    private static bool HasAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static string ToPascalCase(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static void WriteContext(string outputPath, IEnumerable<string> types)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace Nanto.Generated;");
        builder.AppendLine();
        builder.AppendLine("[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(");
        builder.AppendLine("    PropertyNamingPolicy = global::System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,");
        builder.AppendLine("    RespectNullableAnnotations = true,");
        builder.AppendLine("    RespectRequiredConstructorParameters = true,");
        builder.AppendLine("    UseStringEnumConverter = true)]");
        foreach (var type in types)
        {
            builder.Append("[global::System.Text.Json.Serialization.JsonSerializable(typeof(").Append(type).AppendLine("))]");
        }

        builder.AppendLine("internal sealed partial class NantoGeneratedJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext;");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
    }

    private static void WriteTypeScript(string outputPath, IReadOnlyList<NantoContractMember> members)
    {
        var emitter = new TypeScriptEmitter();
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
            builder.Append("    ").Append(group.Key).AppendLine(": {");
            foreach (var member in group.OrderBy(static member => member.MemberName, StringComparer.Ordinal))
            {
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
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
    }

    internal sealed class TypeScriptEmitter
    {
        private readonly HashSet<ITypeSymbol> _declared = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, string> _names = new(SymbolEqualityComparer.Default);
        private readonly HashSet<ITypeSymbol> _types = new(SymbolEqualityComparer.Default);

        internal StringBuilder Declarations { get; } = new();

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
            Declarations.Append("export const ").Append(name).AppendLine(" = {");
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue)
                .OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
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
            Declarations.Append("export interface ").Append(_names[type]).AppendLine(" {");
            foreach (var property in propertyTypes)
            {
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
