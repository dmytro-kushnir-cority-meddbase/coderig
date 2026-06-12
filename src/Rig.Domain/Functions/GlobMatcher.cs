using System.Text.RegularExpressions;

namespace Rig.Domain.Functions;

public static class GlobMatcher
{
    public static bool IsMatch(string value, string glob)
    {
        return Regex.IsMatch(value.Replace('\\', '/'), ToRegex(glob), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string ToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var regex = Regex
            .Escape(normalized)
            .Replace("\\*\\*/", "(?:.*/)?")
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", ".");

        return $"^{regex}$";
    }
}
