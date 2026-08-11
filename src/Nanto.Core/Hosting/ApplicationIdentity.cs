using System.Security.Cryptography;
using System.Text;

namespace Nanto.Hosting;

/// <summary>
/// Represents the canonical application identifier and its stable filesystem-safe storage key.
/// </summary>
public sealed record ApplicationIdentity
{
    /// <summary>
    /// Gets the normalized application identifier.
    /// </summary>
    public string CanonicalId { get; }

    /// <summary>
    /// Gets the stable filesystem-safe key derived from the canonical identifier.
    /// </summary>
    public string StorageKey { get; }

    private ApplicationIdentity(string canonicalId, string storageKey)
    {
        CanonicalId = canonicalId;
        StorageKey = storageKey;
    }

    /// <summary>
    /// Validates and canonicalizes an application identifier.
    /// </summary>
    public static ApplicationIdentity Parse(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        var canonicalId = applicationId.Trim().ToLowerInvariant();
        if (canonicalId.Length > 253)
        {
            throw new ArgumentOutOfRangeException(nameof(applicationId), applicationId, "An application ID cannot exceed 253 characters.");
        }

        var segments = canonicalId.Split('.');
        if (segments.Length < 2 || segments.Any(static segment => !IsValidSegment(segment)))
        {
            throw new ArgumentException(
                "An application ID must contain at least two dot-separated ASCII segments made of letters, digits, and interior hyphens.",
                nameof(applicationId));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalId));
        var suffix = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        return new ApplicationIdentity(canonicalId, $"{segments[^1]}-{suffix}");
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length is < 1 or > 63 || segment[0] == '-' || segment[^1] == '-')
        {
            return false;
        }

        foreach (var character in segment)
        {
            if (!((character is >= 'a' and <= 'z') || (character is >= '0' and <= '9') || character == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
