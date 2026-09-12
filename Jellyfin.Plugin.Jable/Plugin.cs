using Jellyfin.Plugin.Jable.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Jable;

/// <summary>Jable plugin entry point.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">The Jellyfin application paths.</param>
    /// <param name="xmlSerializer">The configuration serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        // Migrate credentials embedded in legacy XML URLs after all XML properties have loaded.
        if (Configuration.UrlPassword is { } password) Configuration.ProxyPassword = password;
        if (Configuration.UrlUsername is { } username) Configuration.ProxyUsername = username;
    }

    /// <summary>Gets the active plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Jable";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7378435d-77d2-4ef4-8e7f-c1269f624b24");

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration next) throw new ArgumentException("Invalid Jable configuration.", nameof(configuration));
        next.Validate();
        if (next.UrlUsername is { } username) next.ProxyUsername = username;
        next.ProxyPassword = next.ClearPassword ? string.Empty : !string.IsNullOrEmpty(next.PasswordInput) ? next.PasswordInput : next.UrlPassword ?? Configuration.ProxyPassword;
        next.PasswordInput = null;
        next.ClearPassword = false;
        next.UrlPassword = null;
        next.UrlUsername = null;
        base.UpdateConfiguration(next);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}
