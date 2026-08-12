using System.Text;

namespace Nanto.Hosting.Windows;

internal static class NavigationPolicy
{
    public const string ApplicationHostName = "app.nanto.invalid";

    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    public static bool IsAllowed(string candidate, IReadOnlySet<string> assetPaths)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(assetPaths);
        if (!TrySplitAbsoluteUri(candidate, out var scheme, out _, out var escapedPath))
        {
            return false;
        }

        if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidate.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || !HasValidEscapes(candidate)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var parsedUri)
            || !string.IsNullOrEmpty(parsedUri.UserInfo))
        {
            return false;
        }

        if (!parsedUri.Host.Equals(ApplicationHostName, StringComparison.OrdinalIgnoreCase))
        {
            return !parsedUri.Host.TrimEnd('.').Equals(ApplicationHostName, StringComparison.OrdinalIgnoreCase);
        }

        if (!scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || !parsedUri.IsDefaultPort)
        {
            return false;
        }

        return TryNormalizeApplicationPath(escapedPath, out var path) && assetPaths.Contains(path);
    }

    public static bool IsInitialRouteAllowed(string route, IReadOnlySet<string> assetPaths) =>
        IsAllowed($"https://{ApplicationHostName}{route}", assetPaths);

    private static bool HasValidEscapes(string candidate)
    {
        for (var index = 0; index < candidate.Length; index++)
        {
            if (candidate[index] != '%')
            {
                continue;
            }

            if (index + 2 >= candidate.Length
                || !Uri.IsHexDigit(candidate[index + 1])
                || !Uri.IsHexDigit(candidate[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool TryDecodeSegment(string segment, out string decoded)
    {
        decoded = string.Empty;
        using var bytes = new MemoryStream(segment.Length);
        var literalStart = 0;
        for (var index = 0; index < segment.Length; index++)
        {
            if (segment[index] != '%')
            {
                continue;
            }

            if (index + 2 >= segment.Length || !byte.TryParse(segment.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                return false;
            }

            if (index > literalStart)
            {
                var literal = Encoding.UTF8.GetBytes(segment.AsSpan(literalStart, index - literalStart).ToString());
                bytes.Write(literal);
            }

            bytes.WriteByte(value);
            index += 2;
            literalStart = index + 1;
        }

        if (literalStart < segment.Length)
        {
            var literal = Encoding.UTF8.GetBytes(segment.AsSpan(literalStart).ToString());
            bytes.Write(literal);
        }

        try
        {
            decoded = _strictUtf8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryNormalizeApplicationPath(string escapedPath, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (escapedPath.Length == 0 || escapedPath[0] != '/' || escapedPath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = escapedPath[1..].Split('/');
        if (segments.Any(segment => segment.Length == 0))
        {
            return false;
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (!TryDecodeSegment(segments[index], out var decoded)
                || decoded is "." or ".."
                || decoded.Contains('/')
                || decoded.Contains('\\')
                || decoded.Contains('%')
                || decoded.Contains(':')
                || decoded.Contains('?')
                || decoded.Contains('#')
                || decoded.Any(char.IsControl))
            {
                return false;
            }

            var normalized = decoded.Normalize(NormalizationForm.FormC);
            if (!string.Equals(decoded, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            segments[index] = normalized;
        }

        normalizedPath = $"/{string.Join('/', segments)}";
        return true;
    }

    private static bool TrySplitAbsoluteUri(string candidate, out string scheme, out string authority, out string escapedPath)
    {
        scheme = string.Empty;
        authority = string.Empty;
        escapedPath = string.Empty;
        var schemeEnd = candidate.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        scheme = candidate[..schemeEnd];
        if (!char.IsAsciiLetter(scheme[0]) || scheme.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '+' and not '-' and not '.'))
        {
            return false;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = candidate.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = candidate.Length;
        }

        authority = candidate[authorityStart..authorityEnd];
        if (authority.Length == 0)
        {
            return false;
        }

        var pathEnd = candidate.IndexOfAny(['?', '#'], authorityEnd);
        if (pathEnd < 0)
        {
            pathEnd = candidate.Length;
        }

        escapedPath = authorityEnd < candidate.Length && candidate[authorityEnd] == '/'
            ? candidate[authorityEnd..pathEnd]
            : "/";
        return true;
    }

}