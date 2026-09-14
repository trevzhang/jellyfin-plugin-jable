using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using System.Reflection;
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
    public void BrowserBridgeDefaultsAreDisabled()
    {
        var config = new PluginConfiguration();
        Assert.Equal(string.Empty, config.BrowserBridgeUrl);
        Assert.Equal(string.Empty, config.BrowserBridgeToken);
    }

    [Fact]
    public void BrowserBridgeTokenIsExcludedFromConfigurationJson()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new PluginConfiguration
        {
            BrowserBridgeUrl = "http://bridge:3000/",
            BrowserBridgeToken = "secret"
        });
        Assert.Contains("BrowserBridgeUrl", json);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("BrowserBridgeToken", json);
    }

    [Fact]
    public void BrowserBridgeUpdateResolvesTokenBeforeValidating()
    {
        var plugin = CreatePlugin(new PluginConfiguration { BrowserBridgeUrl = "http://bridge:3000/", BrowserBridgeToken = "old-secret" });

        plugin.UpdateConfiguration(new PluginConfiguration { BrowserBridgeUrl = "http://bridge:3000/" });
        Assert.Equal("old-secret", plugin.Configuration.BrowserBridgeToken);

        plugin.UpdateConfiguration(new PluginConfiguration { BrowserBridgeUrl = "http://bridge:3000/", NewBrowserBridgeToken = "new-secret" });
        Assert.Equal("new-secret", plugin.Configuration.BrowserBridgeToken);

        plugin.UpdateConfiguration(new PluginConfiguration { ClearBrowserBridgeToken = true });
        Assert.Empty(plugin.Configuration.BrowserBridgeToken);
    }

    [Fact]
    public void BrowserBridgeUpdateRejectsClearingTokenWhileUrlIsRetained()
    {
        var plugin = CreatePlugin(new PluginConfiguration { BrowserBridgeUrl = "http://bridge:3000/", BrowserBridgeToken = "secret" });

        Assert.Throws<ArgumentException>(() => plugin.UpdateConfiguration(new PluginConfiguration
        {
            BrowserBridgeUrl = "http://bridge:3000/",
            ClearBrowserBridgeToken = true
        }));
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

    private static Plugin CreatePlugin(PluginConfiguration saved)
    {
        var paths = DispatchProxy.Create<IApplicationPaths, FinalRegressionTests.Proxy>();
        ((FinalRegressionTests.Proxy)(object)paths).Call = (_, _) => Path.GetTempPath();
        var serializer = DispatchProxy.Create<IXmlSerializer, FinalRegressionTests.Proxy>();
        ((FinalRegressionTests.Proxy)(object)serializer).Call = (method, _) => method.Name == "DeserializeFromFile" ? saved : null;
        return new Plugin(paths, serializer);
    }
}
