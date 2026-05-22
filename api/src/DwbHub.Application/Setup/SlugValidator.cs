using System.Text.RegularExpressions;

namespace DwbHub.Application.Setup;

/// <summary>
/// Validates tenant slugs against the regex defined in Spec 0.3d §2.6.
/// Lower-case + digits, 1-32 chars, hyphens allowed inside but not at edges.
/// Mirrors the CITEXT UNIQUE constraint already on `tenants.slug`.
/// </summary>
public static class SlugValidator
{
    private static readonly Regex Pattern = new(
        @"^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$",
        RegexOptions.Compiled);

    public static bool IsValid(string? slug)
        => !string.IsNullOrEmpty(slug) && Pattern.IsMatch(slug);
}
