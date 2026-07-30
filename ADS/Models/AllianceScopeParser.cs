using System.Text.RegularExpressions;

namespace ADS.Models;

public static partial class AllianceScopeParser
{
    [GeneratedRegex(@"(?<![\p{L}\p{N}])([ABC])(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneAllianceRegex();

    public static string? Parse(bool isAlliance, string? partyTypeText)
    {
        if (!isAlliance || string.IsNullOrWhiteSpace(partyTypeText))
            return null;

        var matches = StandaloneAllianceRegex().Matches(partyTypeText);
        return matches.Count == 1
            ? matches[0].Groups[1].Value.ToUpperInvariant()
            : null;
    }

    public static bool IsValidScope(string? alliance)
    {
        var trimmed = alliance?.Trim();
        return trimmed is not null
               && trimmed.Length == 1
               && trimmed[0] is 'A' or 'a' or 'B' or 'b' or 'C' or 'c';
    }
}
