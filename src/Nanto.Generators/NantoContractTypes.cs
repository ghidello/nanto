using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.CodeAnalysis;

namespace Nanto.Generators;

public enum NantoContractMemberKind
{
    Command,
    Stream,
    Event,
}

public sealed class NantoContractParameter
{
    public string Name { get; }

    public ITypeSymbol Type { get; }

    public NantoContractParameter(string name, ITypeSymbol type)
    {
        Name = name;
        Type = type;
    }
}

public sealed class NantoContractMember
{
    public INamedTypeSymbol ContainingType { get; }

    public INamedTypeSymbol GroupType { get; }

    public ISymbol Symbol { get; }

    public string GroupName { get; }

    public string MemberName { get; }

    public string SymbolicName { get; }

    public string CanonicalSignature { get; }

    public string ManifestEntry { get; }

    public uint Id { get; }

    public NantoContractMemberKind Kind { get; }

    public IReadOnlyList<NantoContractParameter> Parameters { get; }

    public ITypeSymbol? PayloadType { get; }

    public NantoContractMember(
        INamedTypeSymbol containingType,
        INamedTypeSymbol groupType,
        ISymbol symbol,
        string groupName,
        string memberName,
        string symbolicName,
        string canonicalSignature,
        string manifestEntry,
        uint id,
        NantoContractMemberKind kind,
        IReadOnlyList<NantoContractParameter> parameters,
        ITypeSymbol? payloadType)
    {
        ContainingType = containingType;
        GroupType = groupType;
        Symbol = symbol;
        GroupName = groupName;
        MemberName = memberName;
        SymbolicName = symbolicName;
        CanonicalSignature = canonicalSignature;
        ManifestEntry = manifestEntry;
        Id = id;
        Kind = kind;
        Parameters = parameters;
        PayloadType = payloadType;
    }
}

public static class NantoContractTypes
{
    public static bool TryCreateMember(
        INamedTypeSymbol containingType,
        ISymbol symbol,
        out NantoContractMember? member,
        out INamedTypeSymbol? invalidGroup)
    {
        var groupType = containingType;
        invalidGroup = null;
        foreach (var attribute in containingType.GetAttributes())
        {
            if (attribute.AttributeClass is not { IsGenericType: true, Name: "NantoApiPartAttribute" } attributeClass
                || attributeClass.ContainingNamespace.ToDisplayString() != "Nanto")
            {
                continue;
            }

            groupType = (INamedTypeSymbol)attributeClass.TypeArguments[0];
            if (!HasAttribute(groupType, "NantoApiAttribute"))
            {
                invalidGroup = groupType;
                member = null;
                return false;
            }

            break;
        }

        var groupName = ToCamelCase(RemoveSuffix(groupType.Name, "Api"));
        var isEvent = symbol is IPropertySymbol;
        var memberName = ToCamelCase(isEvent ? symbol.Name : RemoveSuffix(symbol.Name, "Async"));
        var canonicalSignature = CreateCanonicalSignature(groupName, memberName, symbol);
        var manifestEntry = CreateManifestEntry(canonicalSignature, symbol);
        var kind = isEvent
            ? NantoContractMemberKind.Event
            : ((IMethodSymbol)symbol).ReturnType is INamedTypeSymbol { Name: "IAsyncEnumerable" }
                ? NantoContractMemberKind.Stream
                : NantoContractMemberKind.Command;
        var parameters = symbol is IMethodSymbol commandMethod
            ? commandMethod.Parameters.Where(static parameter => !IsInjected(parameter))
                .Select(static parameter => new NantoContractParameter(parameter.Name, parameter.Type))
                .ToArray()
            : [];
        var payloadType = symbol switch
        {
            IMethodSymbol payloadMethod => GetPayloadType(payloadMethod.ReturnType),
            IPropertySymbol { Type: INamedTypeSymbol { TypeArguments.Length: 1 } eventType } => eventType.TypeArguments[0],
            _ => null,
        };
        member = new NantoContractMember(
            containingType,
            groupType,
            symbol,
            groupName,
            memberName,
            groupName + "." + memberName,
            canonicalSignature,
            manifestEntry,
            ComputeId(canonicalSignature),
            kind,
            parameters,
            payloadType);
        return true;
    }

    public static uint ComputeId(string canonicalSignature)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalSignature));
        return ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
    }

    public static string CreateManifestEntry(string canonicalSignature, ISymbol symbol)
    {
        var schemas = new SortedSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in GetSerializationTypes(symbol))
        {
            AddSchema(type, schemas, visiting);
        }

        return canonicalSignature + "|schemas:" + string.Join(";", schemas);
    }

    public static string ComputeManifestFingerprint(IEnumerable<string> manifestEntries, int protocolVersion)
    {
        var manifest = string.Join("\n", manifestEntries.OrderBy(static value => value, StringComparer.Ordinal));
        var bytes = Encoding.UTF8.GetBytes("v" + protocolVersion.ToString(CultureInfo.InvariantCulture) + "\n" + manifest);
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static IReadOnlyList<ITypeSymbol> GetSerializationTypes(ISymbol symbol)
    {
        var types = new List<ITypeSymbol>();
        if (symbol is IMethodSymbol method)
        {
            foreach (var parameter in method.Parameters.Where(static parameter => !IsInjected(parameter)))
            {
                types.Add(parameter.Type);
            }

            if (GetPayloadType(method.ReturnType) is { } payload)
            {
                AddPayloadTypes(payload, types);
            }
        }
        else if (symbol is IPropertySymbol { Type: INamedTypeSymbol { TypeArguments.Length: 1 } eventType })
        {
            types.Add(eventType.TypeArguments[0]);
        }

        return types;
    }

    public static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value) || char.IsLower(value[0]))
        {
            return value;
        }

        var characters = value.ToCharArray();
        var index = 0;
        while (index < characters.Length && char.IsUpper(characters[index]))
        {
            var hasNext = index + 1 < characters.Length;
            if (index > 0 && hasNext && char.IsLower(characters[index + 1]))
            {
                break;
            }

            characters[index] = char.ToLowerInvariant(characters[index]);
            index++;
        }

        return new string(characters);
    }

    private static bool HasAttribute(INamedTypeSymbol type, string name) =>
        type.GetAttributes().Any(attribute => attribute.AttributeClass is { Name: var attributeName } attributeClass
            && attributeName == name
            && attributeClass.ContainingNamespace.ToDisplayString() == "Nanto");

    private static string CreateCanonicalSignature(string groupName, string memberName, ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            var parameters = string.Join(",", method.Parameters.Select(static parameter =>
                parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            return "command:" + groupName + "." + memberName + "(" + parameters + ")->"
                + method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        var property = (IPropertySymbol)symbol;
        return "event:" + groupName + "." + memberName + "->" + property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static string RemoveSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length ? value.Substring(0, value.Length - suffix.Length) : value;

    private static void AddPayloadTypes(ITypeSymbol payload, List<ITypeSymbol> types)
    {
        if (payload is INamedTypeSymbol { Name: "NantoResult", TypeArguments.Length: 2 } result
            && result.ContainingNamespace.ToDisplayString() == "Nanto")
        {
            types.Add(result.TypeArguments[0]);
            types.Add(result.TypeArguments[1]);
            return;
        }

        types.Add(payload);
    }

    private static void AddSchema(ITypeSymbol type, ISet<string> schemas, ISet<string> visiting)
    {
        var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (schemas.Any(schema => schema.StartsWith(typeName + "=", StringComparison.Ordinal)))
        {
            return;
        }

        if (!visiting.Add(typeName))
        {
            schemas.Add(typeName + "=<cycle>");
            return;
        }

        try
        {
            if (type is IArrayTypeSymbol array)
            {
                AddSchema(array.ElementType, schemas, visiting);
                schemas.Add(typeName + "=array(" + array.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")");
                return;
            }

            if (type is not INamedTypeSymbol named || named.SpecialType != SpecialType.None)
            {
                schemas.Add(typeName + "=scalar");
                return;
            }

            foreach (var argument in named.TypeArguments)
            {
                AddSchema(argument, schemas, visiting);
            }

            if (named.TypeKind == TypeKind.Enum)
            {
                var values = named.GetMembers().OfType<IFieldSymbol>()
                    .Where(static field => field.HasConstantValue)
                    .OrderBy(static field => field.Name, StringComparer.Ordinal)
                    .Select(static field => field.Name + "=" + Convert.ToString(field.ConstantValue, CultureInfo.InvariantCulture));
                schemas.Add(typeName + "=enum(" + string.Join(",", values) + ")");
                return;
            }

            if (named.ContainingNamespace.ToDisplayString().StartsWith("System", StringComparison.Ordinal))
            {
                schemas.Add(typeName + "=system");
                return;
            }

            var properties = named.GetMembers().OfType<IPropertySymbol>()
                .Where(static property => !property.IsStatic && property.GetMethod is { DeclaredAccessibility: Accessibility.Public })
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .ToArray();
            foreach (var property in properties)
            {
                AddSchema(property.Type, schemas, visiting);
            }

            schemas.Add(typeName + "=object(" + string.Join(",", properties.Select(static property =>
                property.Name + ":" + property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))) + ")");
        }
        finally
        {
            visiting.Remove(typeName);
        }
    }

    private static ITypeSymbol? GetPayloadType(ITypeSymbol returnType) =>
        returnType is INamedTypeSymbol { TypeArguments.Length: 1 } named ? named.TypeArguments[0] : null;

    private static bool IsInjected(IParameterSymbol parameter)
    {
        var name = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return name is "global::Nanto.NantoCommandContext" or "global::System.Threading.CancellationToken";
    }
}
