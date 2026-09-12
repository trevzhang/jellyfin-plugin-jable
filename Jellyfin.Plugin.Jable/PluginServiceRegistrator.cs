using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.Jable.Services;

namespace Jellyfin.Plugin.Jable;

/// <summary>Registers shared plugin services for Jellyfin's concrete type discovery.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<JableParser>();
        services.AddSingleton<JableHttpClient>();
        services.AddSingleton<CatalogStore>();
        services.AddSingleton<JableCatalogService>();
        services.AddSingleton<LibraryAccessService>();
    }
}
