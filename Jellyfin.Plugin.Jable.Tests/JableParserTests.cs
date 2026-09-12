using Jellyfin.Plugin.Jable.Services;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class JableParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Page = new("https://jable.tv/videos/ssis-123/");
    private readonly JableParser _parser = new();

    [Fact]
    public void ParseListExtractsCountsAndIdentity()
    {
        var page = _parser.ParseList(Fixture("list.html"), new Uri("https://jable.tv/latest-updates/"), Now);

        Assert.Equal(2, page.Works.Count);
        Assert.Equal("SSIS-123", page.Works[0].Number);
        Assert.Equal(2_300_000, page.Works[0].ViewCount);
        Assert.Equal(18_000, page.Works[0].FavoriteCount);
        Assert.Equal("https://jable.tv/latest-updates/2/", page.NextPageUri?.AbsoluteUri);
    }

    [Fact]
    public void ParseDetailExtractsValidatedMetadata()
    {
        var work = _parser.ParseDetail(Fixture("detail.html"), "SSIS-123", Page, Now);

        Assert.NotNull(work);
        Assert.Equal("SSIS-123", work.Number);
        Assert.Equal("SSIS-123 Example title", work.Title);
        Assert.Equal("https://jable.tv/videos/ssis-123/", work.CanonicalUrl);
        Assert.Equal("https://assets.jable.tv/contents/videos/SSIS-123/cover.jpg", work.PosterUrl);
        Assert.Equal(Now.AddDays(-3), work.ReleaseDate);
        Assert.Equal(["演员甲"], work.Actresses);
        Assert.Equal(["中文字幕", "单体作品"], work.Genres);
    }

    [Fact]
    public void ParseDetailRejectsMismatchedIdentity()
    {
        Assert.Null(_parser.ParseDetail(Fixture("detail.html"), "SSIS-124", Page, Now));
    }

    [Fact]
    public void ParseListRejectsNextLinkOutsideJableHosts()
    {
        var html = Fixture("list.html").Replace("https://jable.tv/latest-updates/2/", "https://jable.tv.attacker.invalid/latest-updates/2/", StringComparison.Ordinal);

        Assert.Null(_parser.ParseList(html, new Uri("https://jable.tv/latest-updates/"), Now).NextPageUri);
    }

    [Fact]
    public void ParseDetailRejectsAnUntrustedSourcePage()
    {
        Assert.Null(_parser.ParseDetail(Fixture("detail.html"), "SSIS-123", new Uri("https://jable.tv.attacker.invalid/videos/ssis-123/"), Now));
    }

    [Theory]
    [InlineData("18K", 18000)]
    [InlineData("2.3M", 2300000)]
    [InlineData("9,001", 9001)]
    public void ParseCountSupportsPageFormats(string input, long expected) =>
        Assert.Equal(expected, JableParser.ParseCount(input));

    [Theory]
    [InlineData("-1")]
    [InlineData("1.2Kx")]
    public void ParseCountRejectsInvalidValues(string input) =>
        Assert.Null(JableParser.ParseCount(input));

    [Theory]
    [InlineData("2 小時前", -2)]
    [InlineData("3 天前", -72)]
    [InlineData("1星期前", -168)]
    [InlineData("2个月前", -1440)]
    [InlineData("1年前", -8760)]
    public void ParseRelativeDateSupportsChineseVariants(string input, int hours) =>
        Assert.Equal(Now.AddHours(hours), JableParser.ParseRelativeDate(input, Now));

    [Fact]
    public void ParseRelativeDateRejectsOutOfRangeValues() =>
        Assert.Null(JableParser.ParseRelativeDate("30000000000000000 年前", Now));

    [Fact]
    public void IsChallengePageRecognizesCloudflareFixture() =>
        Assert.True(JableParser.IsChallengePage(Fixture("challenge.html")));

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
