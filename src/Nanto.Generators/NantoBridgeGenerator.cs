using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nanto.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class NantoBridgeGenerator : IIncrementalGenerator
{
    private const string CommandAttributeName = "Nanto.NantoCommandAttribute";
    private const string EventAttributeName = "Nanto.NantoEventAttribute";

    private static readonly DiagnosticDescriptor _duplicateMember = new(
        "NANTO1001",
        "Duplicate generated bridge member",
        "The generated frontend member '{0}' is contributed more than once to API group '{1}'",
        "Nanto.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor _unsupportedCommand = new(
        "NANTO1002",
        "Unsupported command signature",
        "Command '{0}' is not supported: {1}",
        "Nanto.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor _invalidPart = new(
        "NANTO1003",
        "Invalid composed API part",
        "API part '{0}' targets '{1}', which must be marked with [NantoApi]",
        "Nanto.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor _identifierCollision = new(
        "NANTO1004",
        "Generated bridge identifier collision",
        "Bridge members '{0}' and '{1}' produce the same generated identifier {2}",
        "Nanto.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor _duplicateGroup = new(
        "NANTO1005",
        "Duplicate generated bridge group",
        "API types '{0}' and '{1}' both define frontend group '{2}'; use partial types or [NantoApiPart<TApi>] to compose a group",
        "Nanto.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var commands = context.SyntaxProvider.ForAttributeWithMetadataName(
            CommandAttributeName,
            static (node, _) => node is BaseMethodDeclarationSyntax,
            static (attributeContext, _) => (IMethodSymbol)attributeContext.TargetSymbol);

        var events = context.SyntaxProvider.ForAttributeWithMetadataName(
            EventAttributeName,
            static (node, _) => node is PropertyDeclarationSyntax or IndexerDeclarationSyntax,
            static (attributeContext, _) => (IPropertySymbol)attributeContext.TargetSymbol);

        context.RegisterSourceOutput(
            commands.Collect().Combine(events.Collect()),
            static (productionContext, input) => Generate(productionContext, input.Left, input.Right));
    }

    private static void Generate(
        SourceProductionContext context,
        ImmutableArray<IMethodSymbol> commandSymbols,
        ImmutableArray<IPropertySymbol> eventSymbols)
    {
        var members = new List<NantoContractMember>(commandSymbols.Length + eventSymbols.Length);

        foreach (var command in commandSymbols)
        {
            var failure = ValidateCommand(command);
            if (failure is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(_unsupportedCommand, command.Locations.FirstOrDefault(), command.Name, failure));
                continue;
            }

            if (!NantoContractTypes.TryCreateMember(command.ContainingType, command, out var member, out var invalidGroup))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    _invalidPart,
                    command.Locations.FirstOrDefault(),
                    command.ContainingType.ToDisplayString(),
                    invalidGroup!.ToDisplayString()));
                continue;
            }

            members.Add(member!);
        }

        foreach (var eventProperty in eventSymbols)
        {
            var failure = ValidateEvent(eventProperty);
            if (failure is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    _unsupportedCommand,
                    eventProperty.Locations.FirstOrDefault(),
                    eventProperty.Name,
                    failure));
                continue;
            }

            failure = ValidateSerializationTypes(eventProperty);
            if (failure is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(_unsupportedCommand, eventProperty.Locations.FirstOrDefault(), eventProperty.Name, failure));
                continue;
            }

            if (!NantoContractTypes.TryCreateMember(eventProperty.ContainingType, eventProperty, out var member, out var invalidGroup))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    _invalidPart,
                    eventProperty.Locations.FirstOrDefault(),
                    eventProperty.ContainingType.ToDisplayString(),
                    invalidGroup!.ToDisplayString()));
                continue;
            }

            members.Add(member!);
        }

        var hasModelErrors = false;
        foreach (var duplicate in members.GroupBy(static member => (member.GroupName, member.MemberName)).Where(static group => group.Count() > 1))
        {
            hasModelErrors = true;
            foreach (var member in duplicate)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    _duplicateMember,
                    member.Symbol.Locations.FirstOrDefault(),
                    member.MemberName,
                    member.GroupName));
            }
        }

        foreach (var group in members.GroupBy(static member => member.GroupName, StringComparer.Ordinal))
        {
            var roots = group.Select(static member => member.GroupType).Distinct(SymbolEqualityComparer.Default).ToArray();
            if (roots.Length <= 1)
            {
                continue;
            }

            hasModelErrors = true;
            foreach (var member in group)
            {
                var other = roots.First(root => !SymbolEqualityComparer.Default.Equals(root, member.GroupType))!;
                context.ReportDiagnostic(Diagnostic.Create(
                    _duplicateGroup,
                    member.Symbol.Locations.FirstOrDefault(),
                    member.GroupType.ToDisplayString(),
                    other.ToDisplayString(),
                    member.GroupName));
            }
        }

        foreach (var collision in members.GroupBy(static member => member.Id).Where(static group => group.Select(member => member.CanonicalSignature).Distinct().Count() > 1))
        {
            hasModelErrors = true;
            var values = collision.OrderBy(static member => member.CanonicalSignature, StringComparer.Ordinal).ToArray();
            foreach (var member in values)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    _identifierCollision,
                    member.Symbol.Locations.FirstOrDefault(),
                    values[0].SymbolicName,
                    values[1].SymbolicName,
                    member.Id.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (members.Count == 0 || hasModelErrors)
        {
            return;
        }

        var orderedMembers = members
            .OrderBy(static member => member.SymbolicName, StringComparer.Ordinal)
            .ThenBy(static member => member.Kind)
            .ToArray();
        var source = Emit(orderedMembers).Replace("\r\n", "\n");
        context.AddSource("NantoBridge.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static string? ValidateCommand(IMethodSymbol command)
    {
        if (!IsAccessibleFromGeneratedCode(command) || !IsAccessibleFromGeneratedCode(command.ContainingType))
        {
            return "the method and its containing types must be accessible from generated code";
        }

        if (command.MethodKind != MethodKind.Ordinary)
        {
            return "only ordinary methods are supported";
        }

        if (command.IsStatic)
        {
            return "static methods are not supported";
        }

        if (command.IsGenericMethod || command.ContainingType.IsGenericType)
        {
            return "open or containing generic methods are not supported";
        }

        if (command.Parameters.Any(static parameter => parameter.IsOptional || parameter.IsParams))
        {
            return "optional and params-array serialized parameters are not supported";
        }

        if (command.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
        {
            return "ref, in, and out parameters are not supported";
        }

        var encounteredInjectedParameter = false;
        var cancellationTokens = 0;
        var contexts = 0;
        foreach (var parameter in command.Parameters)
        {
            var typeName = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var isCancellation = typeName == "global::System.Threading.CancellationToken";
            var isContext = typeName == "global::Nanto.NantoCommandContext";
            if (isCancellation || isContext)
            {
                encounteredInjectedParameter = true;
                cancellationTokens += isCancellation ? 1 : 0;
                contexts += isContext ? 1 : 0;
            }
            else if (encounteredInjectedParameter)
            {
                return "injected NantoCommandContext and CancellationToken parameters must follow serialized parameters";
            }
        }

        if (cancellationTokens > 1 || contexts > 1)
        {
            return "a command may contain at most one NantoCommandContext and one CancellationToken";
        }

        if (!IsSupportedReturnType(command.ReturnType))
        {
            return "the return type must be Task, Task<T>, ValueTask, ValueTask<T>, or IAsyncEnumerable<T>";
        }

        if (command.ReturnType is INamedTypeSymbol { TypeArguments.Length: 1 } asyncType
            && asyncType.TypeArguments[0] is INamedTypeSymbol { OriginalDefinition.Name: "NantoResult", TypeArguments.Length: 2 } result
            && SymbolEqualityComparer.Default.Equals(result.TypeArguments[0], result.TypeArguments[1]))
        {
            return "NantoResult<T, TError> requires different value and error types";
        }

        return ValidateSerializationTypes(command);
    }

    private static string? ValidateEvent(IPropertySymbol eventProperty)
    {
        if (!IsAccessibleFromGeneratedCode(eventProperty)
            || !IsAccessibleFromGeneratedCode(eventProperty.ContainingType)
            || eventProperty.GetMethod is null
            || !IsAccessibleFromGeneratedCode(eventProperty.GetMethod))
        {
            return "the property, getter, and containing types must be accessible from generated code";
        }

        if (eventProperty.IsStatic)
        {
            return "static event properties are not supported";
        }

        if (eventProperty.IsIndexer)
        {
            return "indexed event properties are not supported";
        }

        return IsNantoEvent(eventProperty.Type) ? null : "[NantoEvent] properties must have type NantoEvent<T>";
    }

    private static bool IsAccessibleFromGeneratedCode(ISymbol symbol) => symbol.DeclaredAccessibility is
        Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

    private static string? ValidateSerializationTypes(ISymbol symbol)
    {
        var visiting = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var validated = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var type in NantoContractTypes.GetSerializationTypes(symbol))
        {
            if (ValidateSerializationType(type, visiting, validated) is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    private static string? ValidateSerializationType(
        ITypeSymbol type,
        ISet<ITypeSymbol> visiting,
        ISet<ITypeSymbol> validated)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.None);
        if (validated.Contains(type))
        {
            return null;
        }

        if (!visiting.Add(type))
        {
            return "cyclic object graphs are not supported";
        }

        try
        {
            if (type is ITypeParameterSymbol || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Dynamic)
            {
                return "open, pointer, function-pointer, and dynamic types are not supported";
            }

            if (type.TypeKind == TypeKind.Delegate)
            {
                return "delegates are not supported";
            }

            if (type.SpecialType is SpecialType.System_Object or SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
            {
                return "object and platform handle types are not supported";
            }

            if (type is INamedTypeSymbol handleType && InheritsFrom(handleType, "System.Runtime.InteropServices.SafeHandle"))
            {
                return "platform handle types are not supported";
            }

            if (type.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Decimal)
            {
                return "64-bit integer and decimal values require a lossless wire representation and are not supported";
            }

            if (type is IArrayTypeSymbol array)
            {
                if (array.ElementType.SpecialType == SpecialType.System_Byte)
                {
                    return "binary values are deferred and byte arrays are not supported in Phase 2";
                }

                return ValidateSerializationType(array.ElementType, visiting, validated);
            }

            if (type is not INamedTypeSymbol named)
            {
                return null;
            }

            if (HasJsonSerializationAttribute(named))
            {
                return "custom JSON serialization and polymorphic DTOs are not supported";
            }

            if (named.IsUnboundGenericType || named.TypeArguments.Any(static argument => argument is ITypeParameterSymbol))
            {
                return "open generic types are not supported";
            }

            foreach (var argument in named.TypeArguments)
            {
                if (ValidateSerializationType(argument, visiting, validated) is { } argumentFailure)
                {
                    return argumentFailure;
                }
            }

            if (named.TypeKind == TypeKind.Enum)
            {
                if (named.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
                {
                    return "flags enums are not supported because combined values do not have a closed string-union representation";
                }

                validated.Add(type);
                return null;
            }

            var namespaceName = named.ContainingNamespace.ToDisplayString();
            if (namespaceName.StartsWith("System", StringComparison.Ordinal))
            {
                if (namespaceName == "System" && named.Name is "Memory" or "ReadOnlyMemory"
                    && named.TypeArguments.Length == 1
                    && named.TypeArguments[0].SpecialType == SpecialType.System_Byte)
                {
                    return "binary values are deferred and byte memory is not supported in Phase 2";
                }

                if (namespaceName.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal))
                {
                    return "platform handle types are not supported";
                }

                if (!IsSupportedSystemSerializationType(named))
                {
                    return $"system type '{named.ToDisplayString()}' does not have a defined TypeScript wire representation";
                }

                validated.Add(type);
                return null;
            }

            if (named.TypeKind == TypeKind.Interface || named.IsAbstract || named.BaseType is { SpecialType: not SpecialType.System_Object })
            {
                return "abstract, interface, inherited, and polymorphic DTOs are not supported";
            }

            var properties = GetSerializableProperties(named).ToArray();
            if (properties.GroupBy(static property => NantoContractTypes.ToCamelCase(property.Name), StringComparer.Ordinal)
                .Any(static group => group.Count() > 1))
            {
                return $"DTO type '{named.ToDisplayString()}' has properties that map to the same camel-case JSON name";
            }

            foreach (var property in properties)
            {
                if (HasJsonSerializationAttribute(property))
                {
                    return "custom JSON serialization is not supported";
                }

                if (ValidateSerializationType(property.Type, visiting, validated) is { } propertyFailure)
                {
                    return propertyFailure;
                }
            }

            validated.Add(type);
            return null;
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    private static IEnumerable<IPropertySymbol> GetSerializableProperties(INamedTypeSymbol type) => type.GetMembers().OfType<IPropertySymbol>()
        .Where(static property => !property.IsStatic && property.GetMethod is { DeclaredAccessibility: Accessibility.Public });

    private static bool InheritsFrom(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == metadataName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSupportedSystemSerializationType(INamedTypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None || type.ContainingNamespace.ToDisplayString() == "System"
            && type.Name is "Guid" or "DateTime" or "DateTimeOffset" or "DateOnly" or "TimeOnly" or "TimeSpan" or "Uri")
        {
            return true;
        }

        if (type.ContainingNamespace.ToDisplayString() != "System.Collections.Generic")
        {
            return false;
        }

        return type.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
            or "List" or "HashSet" or "Queue" or "Stack" or "Dictionary" or "IDictionary" or "IReadOnlyDictionary";
    }

    private static bool IsSupportedReturnType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        var containingNamespace = namedType.ContainingNamespace.ToDisplayString();
        return containingNamespace == "System.Threading.Tasks" && namedType.Name is "Task" or "ValueTask"
            || containingNamespace == "System.Collections.Generic" && namedType.Name == "IAsyncEnumerable" && namedType.TypeArguments.Length == 1;
    }

    private static bool IsNantoEvent(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "NantoEvent", TypeArguments.Length: 1 } namedType
        && namedType.ContainingNamespace.ToDisplayString() == "Nanto";

    private static bool HasJsonSerializationAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization");

    private static string Emit(IReadOnlyList<NantoContractMember> members)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace Nanto.Generated;");
        builder.AppendLine();
        builder.AppendLine("public static class AppCapabilities");
        builder.AppendLine("{");
        foreach (var group in members.GroupBy(static member => member.GroupName).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            builder.Append("    public static class ").Append(ToPascalCase(group.Key)).AppendLine();
            builder.AppendLine("    {");
            foreach (var member in group.OrderBy(static member => member.MemberName, StringComparer.Ordinal))
            {
                builder.Append("        public static global::Nanto.NantoFrontendCapability ")
                    .Append(ToPascalCase(member.MemberName))
                    .Append(" { get; } = new(")
                    .Append(member.Id.ToString(CultureInfo.InvariantCulture)).Append("U, \"")
                    .Append(member.SymbolicName).Append("\", global::Nanto.NantoFrontendCapabilityKind.")
                    .Append(member.Kind is NantoContractMemberKind.Command or NantoContractMemberKind.Stream ? "Command" : "Event").AppendLine(");");
            }

            builder.AppendLine("    }");
        }

        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("internal static class NantoGeneratedBridgeRegistrationExtensions");
        builder.AppendLine("{");
        builder.AppendLine("    private static readonly string[] _manifestEntries =");
        builder.AppendLine("        [");
        foreach (var manifestEntry in members.Select(static member => member.ManifestEntry).Distinct(StringComparer.Ordinal)
            .OrderBy(static manifestEntry => manifestEntry, StringComparer.Ordinal))
        {
            builder.Append("            ").Append(Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(manifestEntry, true)).AppendLine(",");
        }

        builder.AppendLine("        ];");
        builder.AppendLine();
        var byType = members.GroupBy(
                static member => member.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal);
        var index = 0;
        foreach (var typeGroup in byType)
        {
            var typeName = typeGroup.Key;
            builder.Append("    internal static global::Nanto.NantoBridgeConfiguration Add(this global::Nanto.NantoBridgeConfiguration bridge, ")
                .Append(typeName).AppendLine(" api)");
            builder.AppendLine("    {");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(bridge);");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(api);");
            builder.Append("        return bridge.AddGenerated(new Registration").Append(index).AppendLine("(api), _manifestEntries);");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    private sealed class Registration").Append(index).Append('(').Append(typeName)
                .AppendLine(" api) : global::Nanto.NantoGeneratedApiRegistration");
            builder.AppendLine("    {");
            EmitDescriptors(builder, typeGroup.Where(static member => member.Kind != NantoContractMemberKind.Event), "Commands", "api");
            EmitDescriptors(builder, typeGroup.Where(static member => member.Kind == NantoContractMemberKind.Event), "Events", "api");
            foreach (var command in typeGroup.Where(static member => member.Kind != NantoContractMemberKind.Event))
            {
                EmitCommandDescriptor(builder, command, typeName);
            }
            builder.AppendLine("    }");
            builder.AppendLine();
            index++;
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void EmitDescriptors(StringBuilder builder, IEnumerable<NantoContractMember> members, string propertyName, string apiName)
    {
        var values = members.OrderBy(static member => member.SymbolicName, StringComparer.Ordinal).ToArray();
        builder.Append("        public override global::System.Collections.Generic.IReadOnlyList<global::Nanto.NantoGenerated")
            .Append(propertyName == "Commands" ? "Command" : "Event").Append("> ").Append(propertyName).AppendLine(" { get; } =");
        if (values.Length == 0)
        {
            builder.AppendLine("            [];");
            return;
        }

        builder.AppendLine("            [");
        foreach (var member in values)
        {
            if (member.Kind != NantoContractMemberKind.Event)
            {
                builder.Append("                new Command_").Append(ToPascalCase(member.MemberName)).Append('(').Append(apiName).AppendLine("),");
            }
            else
            {
                var property = (IPropertySymbol)member.Symbol;
                var eventType = (INamedTypeSymbol)property.Type;
                var payload = eventType.TypeArguments[0];
                builder.Append("                new global::Nanto.NantoGeneratedEvent<")
                    .Append(payload.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(">(")
                    .Append(member.Id.ToString(CultureInfo.InvariantCulture)).Append("U, \"").Append(member.SymbolicName).Append("\", ")
                    .Append(Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(member.ManifestEntry, true)).Append(", ")
                    .Append(apiName).Append(".@").Append(property.Name).Append(", static value => ")
                    .Append(SerializeExpression("value", payload)).AppendLine("),");
            }
        }

        builder.AppendLine("            ];");
    }

    private static void EmitCommandDescriptor(StringBuilder builder, NantoContractMember member, string typeName)
    {
        var method = (IMethodSymbol)member.Symbol;
        var payload = GetPayloadType(method.ReturnType);
        var isStream = member.Kind == NantoContractMemberKind.Stream;
        builder.Append("        private sealed class Command_").Append(ToPascalCase(member.MemberName)).Append('(').Append(typeName)
            .AppendLine(" api) : global::Nanto.NantoGeneratedCommand");
        builder.AppendLine("        {");
        builder.Append("            public override uint Id { get; } = ").Append(member.Id.ToString(CultureInfo.InvariantCulture)).AppendLine("U;");
        builder.Append("            public override string SymbolicName { get; } = \"").Append(member.SymbolicName).AppendLine("\";");
        builder.Append("            public override string ManifestEntry { get; } = ")
            .Append(Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(member.ManifestEntry, true)).AppendLine(";");
        builder.Append("            public override global::Nanto.NantoGeneratedCommandKind Kind { get; } = global::Nanto.NantoGeneratedCommandKind.")
            .Append(isStream ? "Stream" : "Unary").AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("            public override async global::System.Threading.Tasks.ValueTask<global::Nanto.NantoGeneratedCommandInvocation> InvokeAsync(");
        builder.AppendLine("                global::System.Text.Json.JsonElement arguments,");
        builder.AppendLine("                global::Nanto.NantoCommandContext context,");
        builder.AppendLine("                global::System.Threading.CancellationToken cancellationToken)");
        builder.AppendLine("            {");
        foreach (var parameter in method.Parameters.Where(static parameter => !IsInjected(parameter)))
        {
            var parameterType = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            builder.Append("                ").Append(parameterType).Append(' ').Append('@').Append(parameter.Name).AppendLine(";");
            builder.AppendLine("                try");
            builder.AppendLine("                {");
            builder.Append("                    ").Append('@').Append(parameter.Name).Append(" = (").Append(parameterType)
                .Append(")global::System.Text.Json.JsonSerializer.Deserialize(arguments.GetProperty(\"").Append(parameter.Name)
                .Append("\"), typeof(").Append(parameterType).AppendLine("), NantoGeneratedJsonContext.Default)!;");
            builder.AppendLine("                }");
            builder.AppendLine("                catch (global::System.Exception exception) when (exception is global::System.Text.Json.JsonException");
            builder.AppendLine("                    or global::System.Collections.Generic.KeyNotFoundException");
            builder.AppendLine("                    or global::System.InvalidOperationException");
            builder.AppendLine("                    or global::System.InvalidCastException");
            builder.AppendLine("                    or global::System.NullReferenceException");
            builder.AppendLine("                    or global::System.FormatException");
            builder.AppendLine("                    or global::System.OverflowException)");
            builder.AppendLine("                {");
            builder.AppendLine("                    throw new global::Nanto.NantoGeneratedInvalidRequestException(exception);");
            builder.AppendLine("                }");
            if (parameter.Type.IsReferenceType && parameter.NullableAnnotation != NullableAnnotation.Annotated)
            {
                builder.Append("                if (@").Append(parameter.Name).AppendLine(" is null)");
                builder.AppendLine("                {");
                builder.AppendLine("                    throw new global::Nanto.NantoGeneratedInvalidRequestException(new global::System.Text.Json.JsonException());");
                builder.AppendLine("                }");
            }
        }

        var arguments = string.Join(", ", method.Parameters.Select(static parameter => IsContext(parameter)
            ? "context"
            : IsCancellation(parameter) ? "cancellationToken" : "@" + parameter.Name));
        var call = "api.@" + method.Name + "(" + arguments + ")";
        if (isStream)
        {
            var payloadType = payload!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            builder.Append("                var stream = ").Append(call).AppendLine(";");
            builder.Append("                return new global::Nanto.NantoGeneratedStreamInvocation(new global::Nanto.NantoGeneratedSequence<")
                .Append(payloadType).Append(">(stream, static value => ").Append(SerializeExpression("value", payload!))
                .AppendLine(", cancellationToken));");
        }
        else if (payload is null)
        {
            builder.Append("                await ").Append(call).AppendLine(".ConfigureAwait(false);");
            builder.AppendLine("                return new global::Nanto.NantoGeneratedUnaryInvocation(global::Nanto.NantoGeneratedJson.Null);");
        }
        else
        {
            builder.Append("                var result = await ").Append(call).AppendLine(".ConfigureAwait(false);");
            builder.Append("                return new global::Nanto.NantoGeneratedUnaryInvocation(").Append(SerializeExpression("result", payload)).AppendLine(");");
        }

        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
    }

    private static string SerializeExpression(string value, ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.Name: "NantoResult", TypeArguments.Length: 2 } result)
        {
            var successType = result.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var errorType = result.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return "global::Nanto.NantoGeneratedJson.CreateResult(" + value + ".IsSuccess, " + value + ".IsSuccess"
                + " ? global::System.Text.Json.JsonSerializer.SerializeToElement(" + value + ".Value, typeof(" + successType + "), NantoGeneratedJsonContext.Default)"
                + " : global::System.Text.Json.JsonSerializer.SerializeToElement(" + value + ".Error, typeof(" + errorType + "), NantoGeneratedJsonContext.Default))";
        }

        var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return "global::System.Text.Json.JsonSerializer.SerializeToElement(" + value + ", typeof(" + typeName + "), NantoGeneratedJsonContext.Default)";
    }

    private static ITypeSymbol? GetPayloadType(ITypeSymbol returnType) =>
        returnType is INamedTypeSymbol { TypeArguments.Length: 1 } named ? named.TypeArguments[0] : null;

    private static bool IsInjected(IParameterSymbol parameter) => IsContext(parameter) || IsCancellation(parameter);

    private static bool IsContext(IParameterSymbol parameter) =>
        parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Nanto.NantoCommandContext";

    private static bool IsCancellation(IParameterSymbol parameter) =>
        parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken";

    private static string ToPascalCase(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);
}
