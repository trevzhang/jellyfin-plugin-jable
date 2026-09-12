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

        Assert.Contains("addEventListener('pageshow'", page);
        Assert.DoesNotContain("addEventListener('viewshow'", page);
        Assert.Contains("addEventListener('pageshow', loadConfiguration);", page);
        Assert.Contains("\n            loadConfiguration();", page);
    }
}
