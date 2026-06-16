using System.Text.RegularExpressions;

namespace Rig.Domain.Functions;

public static class GlobMatcher
{
    public static bool IsMatch(string value, string glob)
    {
        return Regex.IsMatch(
            input: value.Replace('\\', '/'),
            pattern: ToRegex(glob),
            options: RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );
    }

    public static string ToRegex(string glob)
    {
        var normalized = glob.Replace(oldChar: '\\', newChar: '/');
        var regex = Regex
            .Escape(normalized)
            .Replace(oldValue: "\\*\\*/", newValue: "(?:.*/)?")
            .Replace(oldValue: "\\*\\*", newValue: ".*")
            .Replace(oldValue: "\\*", newValue: "[^/]*")
            .Replace(oldValue: "\\?", newValue: ".");

        return $"^{regex}$";
    }
}
