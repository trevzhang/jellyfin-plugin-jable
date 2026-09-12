using System.Text.RegularExpressions;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.Jable.Services;

public static class NumberParser
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly (Regex Pattern, Func<Match, string> Format)[] Rules =
    [
        (new(@"(?<![A-Z0-9])FC2(?:[-_ ]?PPV)?[-_ ]?(\d{5,8})(?!\d)", Options), m => $"FC2-PPV-{m.Groups[1].Value}"),
        (new(@"(?<![A-Z0-9])1PONDO[-_ ]?(\d{6})[-_ ](\d{3})(?!\d)", Options), m => $"1PONDO-{m.Groups[1].Value}_{m.Groups[2].Value}"),
        (new(@"(?<!\d)(\d{6})[-_](\d{3})(?!\d)", Options), m => $"{m.Groups[1].Value}-{m.Groups[2].Value}"),
        (new(@"(?<![A-Z0-9])([A-Z][A-Z0-9]{1,11})[-_ ]+(\d{1,7})(?!\d)", Options), m => $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}"),
        (new(@"(?<![A-Z0-9])([A-Z]{2,12})(\d{2,7})(?!\d)", Options), m => $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}"),
    ];

    private static readonly (Regex Pattern, Func<Match, string> Format)[] ExactRules = Rules
        .Select(rule => (new Regex($@"\A(?:{rule.Pattern})\z", Options), rule.Format))
        .ToArray();

    private static readonly Regex VideoExtension = new(@"\.(?:mp4|mkv|avi|mov|m4v|wmv|ts|webm)\z", Options);

    public static string? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(value.TrimEnd('/', '\\'));
        foreach (var (pattern, format) in Rules)
        {
            var match = pattern.Match(name);
            if (match.Success)
            {
                return format(match);
            }
        }

        return null;
    }

    public static string IdentityKey(string value) =>
        Regex.Replace(value, @"[^A-Z0-9]", string.Empty, RegexOptions.IgnoreCase).ToUpperInvariant();

    public static bool IsExact(string left, string right) =>
        NormalizeExact(left) is { } normalizedLeft &&
        NormalizeExact(right) is { } normalizedRight &&
        string.Equals(IdentityKey(normalizedLeft), IdentityKey(normalizedRight), StringComparison.Ordinal);

    private static string? NormalizeExact(string value)
        => string.IsNullOrWhiteSpace(value) ? null : ParseExact(VideoExtension.Replace(value.Trim(), string.Empty));

    public static string? ParseExact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        foreach (var (pattern, format) in ExactRules)
        {
            var match = pattern.Match(candidate);
            if (match.Success)
            {
                return format(match);
            }
        }

        return null;
    }
}
