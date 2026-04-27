using System.Globalization;
using System.Text.RegularExpressions;

namespace DevHub.Sdk.Internal;

internal readonly struct SemanticVersion
{
    internal SemanticVersion(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    internal int Major { get; }

    internal int Minor { get; }

    internal int Patch { get; }
}

internal static class SemanticVersionParser
{
    private static readonly Regex SemanticVersionPattern = new(
        "^(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Compiled);

    internal static bool TryParse(string? value, out SemanticVersion version)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            version = default;
            return false;
        }

        var match = SemanticVersionPattern.Match(value.Trim());
        if (!match.Success)
        {
            version = default;
            return false;
        }

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            version = default;
            return false;
        }

        version = new SemanticVersion(major, minor, patch);
        return true;
    }
}
