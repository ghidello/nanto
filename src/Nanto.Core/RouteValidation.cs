namespace Nanto;

internal static class RouteValidation
{
    public static void ThrowIfInvalid(string route, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route, parameterName);

        if (route[0] != '/' || route.StartsWith("//", StringComparison.Ordinal) || route.Contains('\\'))
        {
            throw new ArgumentException("A route must be root-relative and cannot be scheme-relative or contain backslashes.", parameterName);
        }

        if (Uri.TryCreate(route, UriKind.Absolute, out _))
        {
            throw new ArgumentException("A route cannot be an absolute URI.", parameterName);
        }

        var path = route.AsSpan(0, route.IndexOfAny(['?', '#']) is var suffix && suffix >= 0 ? suffix : route.Length);
        foreach (var segment in path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\'))
            {
                throw new ArgumentException("A route cannot contain dot segments or encoded traversal separators.", parameterName);
            }
        }
    }
}