using System.Globalization;
using System.Text.RegularExpressions;

namespace DevHub.Sdk.Internal;

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch);

internal static partial class SemanticVersionParser
{
    internal static bool TryParse(string? value, out SemanticVersion version)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            version = default;
            return false;
        }

        var match = SemanticVersionPattern().Match(value.Trim());
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

    [GeneratedRegex(
        "^(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex SemanticVersionPattern();
}
