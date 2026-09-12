using Jellyfin.Plugin.Jable.Services;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class NumberParserTests
{
    [Theory]
    [InlineData("SSIS-123.mp4", "SSIS-123")]
    [InlineData("ABP123-C.mp4", "ABP-123")]
    [InlineData("ABP_123.mp4", "ABP-123")]
    [InlineData("FC2 PPV 1234567.mp4", "FC2-PPV-1234567")]
    [InlineData("1PONDO-123456_789.mp4", "1PONDO-123456_789")]
    [InlineData("012318-589.mp4", "012318-589")]
    public void ParseSupportsExistingFilenameFamilies(string input, string expected)
    {
        Assert.Equal(expected, NumberParser.Parse(input));
    }

    [Fact]
    public void ParseSupportsCanonicalVideoPaths()
    {
        Assert.Equal("SSIS-123", NumberParser.Parse("/videos/ssis-123/"));
    }

    [Theory]
    [InlineData("SSIS-123", "ssis_123", true)]
    [InlineData("SSIS-123", "SSIS-1234", false)]
    [InlineData("FC2-PPV-1234567", "FC2-1234567", true)]
    [InlineData("SSIS-123.mp4", "SSIS-123", true)]
    [InlineData("SSIS-123.MKV", "ssis_123", true)]
    [InlineData("SSIS-123.456", "SSIS-123", false)]
    [InlineData("SSIS-123 title 456", "SSIS-123", false)]
    [InlineData("SSIS-123 456", "SSIS-123", false)]
    [InlineData("SSIS-123 SSIS-124", "SSIS-123", false)]
    public void ExactIdentityIgnoresOnlySeparators(string left, string right, bool expected)
    {
        Assert.Equal(expected, NumberParser.IsExact(left, right));
    }
}
