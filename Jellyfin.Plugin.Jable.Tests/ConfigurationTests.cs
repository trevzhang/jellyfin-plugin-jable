using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void DefaultsAreSafeAndBounded()
    {
        var config = new PluginConfiguration();
        Assert.Equal(Guid.Empty, config.SelectedLibraryId);
        Assert.Equal(string.Empty, config.ProxyUrl);
        Assert.Equal(20, config.RecentPageCount);
        Assert.Equal(15, config.RequestTimeoutSeconds);
        Assert.Equal(750, config.MinimumRequestIntervalMs);
    }

    [Fact]
    public void LibrarySelectorPreservesAnEmptyLibraryOnSave()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Jable.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var page = reader.ReadToEnd();

        Assert.Contains("<option value=\"\">No library selected</option>", page);
        Assert.Contains("emptyOption.value = '';", page);
        Assert.Contains("library.replaceChildren(emptyOption);", page);
        Assert.Contains("form.querySelector('#library').value || '00000000-0000-0000-0000-000000000000'", page);
    }

    [Fact]
    public void ConfigurationLoadsOnJellyfinPluginPageEvent()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Jable.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var page = reader.ReadToEnd();

        Assert.Contains("<script type=\"text/javascript\">", page);
        var rootStart = page.IndexOf("<div id=\"JableConfigPage\"", StringComparison.Ordinal);
        var scriptStart = page.IndexOf("<script type=\"text/javascript\">", rootStart, StringComparison.Ordinal);
        var depth = 0;
        foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(page[rootStart..scriptStart], @"</?div\b[^>]*>"))
        {
            depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0) break;
        }
        Assert.True(depth > 0, "The typed configuration script must be inside #JableConfigPage, before its closing div.");
        Assert.Contains("addEventListener('pageshow'", page);
        Assert.DoesNotContain("addEventListener('viewshow'", page);
        Assert.Contains("addEventListener('pageshow', loadConfiguration);", page);
        Assert.Contains("\n            loadConfiguration();", page);
    }
}
