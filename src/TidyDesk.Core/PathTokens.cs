using System.Text.RegularExpressions;

namespace TidyDesk.Core;

public static partial class PathTokens
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Turns an expression like "{Desktop}\Sorted" into a full path. Unknown tokens throw,
    /// so a mistyped rule can never move files somewhere unexpected.
    /// </summary>
    public static string Expand(string value, Func<string, string?> resolver)
    {
        var expanded = TokenRegex().Replace(value, m =>
            resolver(m.Groups[1].Value)
            ?? throw new InvalidOperationException($"Unknown folder token: {m.Value}"));
        expanded = Environment.ExpandEnvironmentVariables(expanded);
        if (!Path.IsPathFullyQualified(expanded))
            throw new InvalidOperationException($"Path must be absolute: {value}");
        return Path.GetFullPath(expanded);
    }
}
