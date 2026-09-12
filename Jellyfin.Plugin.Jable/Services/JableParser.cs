using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Jable.Models;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.Jable.Services;

public sealed class JableParser
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    private static readonly Regex CardPattern = new(@"<div\b(?=[^>]*\bclass\s*=\s*['""][^'""]*\bcol-6\b[^'""]*\bcol-sm-4\b[^'""]*\bcol-lg-3\b[^'""]*['""])[^>]*>(?<card>.*?)</div>", Options);
    private static readonly Regex VideoLinkPattern = new(@"<a\b(?=[^>]*\bhref\s*=\s*['""](?<href>/videos/[^'""]+)['""])[^>]*>", Options);
    private static readonly Regex ImagePattern = new(@"<img\b(?=[^>]*\bdata-src\s*=\s*['""](?<url>[^'""]+)['""])[^>]*>", Options);
    private static readonly Regex TitlePattern = new(@"<h6\b[^>]*>(?<title>.*?)</h6>", Options);
    private static readonly Regex SubtitlePattern = new(@"<p\b(?=[^>]*\bclass\s*=\s*['""][^'""]*\bsub-title\b[^'""]*['""])[^>]*>(?<counts>.*?)</p>", Options);
    private static readonly Regex CountTokenPattern = new(@"\d[\d,]*(?:\.\d+)?[\t ]*[KM]?", Options);
    private static readonly Regex NextLinkPattern = new(@"<a\b(?=[^>]*\brel\s*=\s*['""]next['""])(?=[^>]*\bhref\s*=\s*['""](?<href>[^'""]+)['""])[^>]*>", Options);
    private static readonly Regex DocumentTitlePattern = new(@"<title\b[^>]*>(?<title>.*?)</title>", Options);
    private static readonly Regex CanonicalPattern = new(@"<link\b(?=[^>]*\brel\s*=\s*['""]canonical['""])(?=[^>]*\bhref\s*=\s*['""](?<href>[^'""]+)['""])[^>]*>", Options);
    private static readonly Regex OgImagePattern = new(@"<meta\b(?=[^>]*\bproperty\s*=\s*['""]og:image['""])(?=[^>]*\bcontent\s*=\s*['""](?<url>[^'""]+)['""])[^>]*>", Options);
    private static readonly Regex DatePattern = new(@"(?<amount>\d+)\s*(?<unit>小時|小时|天|日|星期|周|個月|个月|月|年)\s*前", Options);
    private static readonly Regex TagsPattern = new(@"<h5\b(?=[^>]*\bclass\s*=\s*['""][^'""]*\btags\b[^'""]*['""])[^>]*>(?<tags>.*?)</h5>", Options);
    private static readonly Regex AnchorTextPattern = new(@"<a\b[^>]*>(?<text>.*?)</a>", Options);
    private static readonly Regex ActressPattern = new(@"<a\b(?=[^>]*\bhref\s*=\s*['""]/models/[^'""]*['""])[^>]*>(?<text>.*?)</a>", Options);
    private static readonly Regex TagsPatternWhitespace = new(@"\s+", Options);
    private static readonly Regex MarkupPattern = new(@"<[^>]+>", Options);
    private static readonly Regex CountPattern = new(@"\A(?<number>\d+(?:\.\d+)?)(?<suffix>[KM])?\z", Options);
    private static readonly Regex CanonicalPathPattern = new(@"\A/videos/(?<slug>[^/]+)/?\z", Options);
    private static readonly Regex NextPresentPattern = new(@"<a\b[^>]*\brel\s*=\s*['""]next['""]", Options);
    private static readonly Regex StudioPattern = new(@"<span\b(?=[^>]*\bitemprop\s*=\s*['""]productionCompany['""])[^>]*>(?<studio>.*?)</span>", Options);
    private static readonly Regex DurationPattern = new(@"<meta\b(?=[^>]*\bitemprop\s*=\s*['""]duration['""])(?=[^>]*\bcontent\s*=\s*['""](?<duration>[^'""]+)['""])[^>]*>", Options);

    public JableListPage ParseList(string html, Uri pageUri, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUri);

        if (!IsTrustedUri(pageUri))
        {
            return new JableListPage();
        }

        var works = new List<JableWork>();
        foreach (Match cardMatch in CardPattern.Matches(html))
        {
            var card = cardMatch.Groups["card"].Value;
            var link = VideoLinkPattern.Match(card);
            var title = Text(TitlePattern.Match(card).Groups["title"].Value);
            if (!link.Success || !TryTrustedUri(link.Groups["href"].Value, pageUri, out var canonical) || NumberParser.Parse(title) is not { } number || CanonicalNumber(canonical) is not { } canonicalNumber || !NumberParser.IsExact(number, canonicalNumber))
            {
                continue;
            }

            var image = ImagePattern.Match(card);
            var subtitle = SubtitlePattern.Match(card);
            var counts = CountTokenPattern.Matches(subtitle.Success ? Text(MarkupPattern.Replace(subtitle.Groups["counts"].Value, " ")) : string.Empty);
            works.Add(new JableWork
            {
                Number = number,
                Title = title,
                CanonicalUrl = canonical.AbsoluteUri,
                PosterUrl = image.Success && TryTrustedUri(image.Groups["url"].Value, null, out var poster) ? poster.AbsoluteUri : string.Empty,
                ViewCount = counts.Count > 0 ? ParseCount(counts[0].Value) : null,
                FavoriteCount = counts.Count > 1 ? ParseCount(counts[1].Value) : null,
                FetchedAt = fetchedAt,
                SourcePage = pageUri.AbsoluteUri,
            });
        }

        var next = NextLinkPattern.Match(html);
        return new JableListPage
        {
            Works = works,
            HasNextLink = NextPresentPattern.IsMatch(html),
            NextPageUri = next.Success && TryTrustedUri(next.Groups["href"].Value, pageUri, out var nextUri) ? nextUri : null,
        };
    }

    public JableWork? ParseDetail(string html, string expectedNumber, Uri pageUri, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(expectedNumber);
        ArgumentNullException.ThrowIfNull(pageUri);

        if (!IsTrustedUri(pageUri))
        {
            return null;
        }

        var title = Text(DocumentTitlePattern.Match(html).Groups["title"].Value);
        const string suffix = " - Jable.TV";
        if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            title = title[..^suffix.Length].TrimEnd();
        }

        var canonicalMatch = CanonicalPattern.Match(html);
        if (!canonicalMatch.Success || !TryTrustedUri(canonicalMatch.Groups["href"].Value, null, out var canonical))
        {
            return null;
        }

        var titleNumber = NumberParser.Parse(title);
        var canonicalNumber = CanonicalNumber(canonical);
        if (titleNumber is null || canonicalNumber is null
            || !NumberParser.IsExact(expectedNumber, titleNumber)
            || !NumberParser.IsExact(expectedNumber, canonicalNumber))
        {
            return null;
        }

        var image = OgImagePattern.Match(html);
        var tags = TagsPattern.Match(html);
        return new JableWork
        {
            Number = canonicalNumber,
            Title = title,
            CanonicalUrl = canonical.AbsoluteUri,
            PosterUrl = image.Success && TryTrustedUri(image.Groups["url"].Value, null, out var poster) ? poster.AbsoluteUri : string.Empty,
            ReleaseDate = ParseRelativeDate(Text(DatePattern.Match(html).Value), fetchedAt),
            Actresses = ActressPattern.Matches(html).Select(match => Text(match.Groups["text"].Value)).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray(),
            Genres = tags.Success ? AnchorTextPattern.Matches(tags.Groups["tags"].Value).Select(match => Text(match.Groups["text"].Value)).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray() : [],
            Studio = Text(StudioPattern.Match(html).Groups["studio"].Value),
            Duration = ParseDuration(DurationPattern.Match(html).Groups["duration"].Value),
            FetchedAt = fetchedAt,
            SourcePage = pageUri.AbsoluteUri,
        };
    }

    public static long? ParseCount(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var compact = new string(input.Where(character => character != ',' && !char.IsWhiteSpace(character)).ToArray());
        var match = CountPattern.Match(compact);
        if (!match.Success || !decimal.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            return null;
        }

        var multiplier = match.Groups["suffix"].Value.Equals("K", StringComparison.OrdinalIgnoreCase) ? 1_000m : match.Groups["suffix"].Value.Equals("M", StringComparison.OrdinalIgnoreCase) ? 1_000_000m : 1m;
        if (number > long.MaxValue / multiplier) return null;
        var count = number * multiplier;
        return count <= long.MaxValue && decimal.Truncate(count) == count ? (long)count : null;
    }

    public static DateTimeOffset? ParseRelativeDate(string? input, DateTimeOffset now)
    {
        var match = DatePattern.Match(input ?? string.Empty);
        if (!match.Success || !long.TryParse(match.Groups["amount"].Value, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        try
        {
            return match.Groups["unit"].Value switch
            {
                "小時" or "小时" => now.AddHours(-amount),
                "天" or "日" => now.AddDays(-amount),
                "星期" or "周" => now.AddDays(-checked(amount * 7)),
                "個月" or "个月" or "月" => now.AddDays(-checked(amount * 30)),
                "年" => now.AddDays(-checked(amount * 365)),
                _ => null,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static bool IsChallengePage(string? html) =>
        !string.IsNullOrEmpty(html) && (html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase) || html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) || html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase));

    public static string? CanonicalNumber(Uri uri)
    {
        if (!IsTrustedUri(uri) || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        var match = CanonicalPathPattern.Match(uri.AbsolutePath);
        return match.Success ? NumberParser.ParseExact(Uri.UnescapeDataString(match.Groups["slug"].Value)) : null;
    }

    private static TimeSpan? ParseDuration(string value)
    {
        if (!value.StartsWith("PT", StringComparison.Ordinal)) return null;
        try { var duration = System.Xml.XmlConvert.ToTimeSpan(value); return duration > TimeSpan.Zero ? duration : null; }
        catch (Exception exception) when (exception is FormatException or OverflowException) { return null; }
    }

    private static bool TryTrustedUri(string value, Uri? baseUri, out Uri uri)
    {
        uri = null!;
        if (!(baseUri is null ? Uri.TryCreate(value, UriKind.Absolute, out var candidate) : Uri.TryCreate(baseUri, value, out candidate)) || !IsTrustedUri(candidate))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool IsTrustedUri(Uri candidate) =>
        candidate.IsAbsoluteUri && candidate.Scheme == Uri.UriSchemeHttps && candidate.UserInfo.Length == 0 &&
        (candidate.Host.Equals("jable.tv", StringComparison.OrdinalIgnoreCase) || candidate.Host.EndsWith(".jable.tv", StringComparison.OrdinalIgnoreCase));

    private static string Text(string html) =>
        TagsPatternWhitespace.Replace(WebUtility.HtmlDecode(MarkupPattern.Replace(html, string.Empty)), " ").Trim();
}
