using MediaBrowser.Model.Plugins;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Jellyfin.Plugin.Jable.Services;

namespace Jellyfin.Plugin.Jable.Configuration;

/// <summary>Stores Jable plugin settings.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    private string _proxyUrl = string.Empty;
    internal string? UrlPassword;
    internal string? UrlUsername;
    internal string? PasswordInput;
    internal bool ClearPassword;
    internal string? BridgeTokenInput;
    internal bool ClearBridgeToken;
    /// <summary>Gets or sets the library that receives Jable metadata.</summary>
    public Guid SelectedLibraryId { get; set; } = Guid.Empty;
    /// <summary>Gets or sets the optional proxy URL.</summary>
    public string ProxyUrl
    {
        get => _proxyUrl;
        set
        {
            _proxyUrl = value?.Trim() ?? string.Empty;
            UrlPassword = null;
            UrlUsername = null;
            if (Uri.TryCreate(_proxyUrl, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
            {
                var parts = uri.UserInfo.Split(':', 2);
                ProxyUsername = UrlUsername = Uri.UnescapeDataString(parts[0]);
                if (parts.Length == 2 && parts[1].Length > 0) UrlPassword = Uri.UnescapeDataString(parts[1]);
                _proxyUrl = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri;
            }
        }
    }
    /// <summary>Gets or sets the optional proxy user name.</summary>
    public string ProxyUsername { get; set; } = string.Empty;
    /// <summary>Gets or sets the optional proxy password.</summary>
    [JsonIgnore]
    public string ProxyPassword { get; set; } = string.Empty;
    /// <summary>Accepts a new password without returning it in JSON or persisting the input field.</summary>
    [XmlIgnore, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewProxyPassword { get => null; set => PasswordInput = value; }
    /// <summary>Accepts an explicit password clear request.</summary>
    [XmlIgnore, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ClearProxyPassword { get => false; set => ClearPassword = value; }
    /// <summary>Gets or sets the optional browser bridge URL.</summary>
    public string BrowserBridgeUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the optional browser bridge token.</summary>
    [JsonIgnore]
    public string BrowserBridgeToken { get; set; } = string.Empty;
    /// <summary>Accepts a new browser bridge token without returning it in JSON or persisting the input field.</summary>
    [XmlIgnore, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewBrowserBridgeToken { get => null; set => BridgeTokenInput = value; }
    /// <summary>Accepts an explicit browser bridge token clear request.</summary>
    [XmlIgnore, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ClearBrowserBridgeToken { get => false; set => ClearBridgeToken = value; }
    /// <summary>Gets or sets the number of recent catalog pages to fetch.</summary>
    public int RecentPageCount { get; set; } = 20;
    /// <summary>Gets or sets the request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;
    /// <summary>Gets or sets the minimum interval between requests in milliseconds.</summary>
    public int MinimumRequestIntervalMs { get; set; } = 750;

    internal void Validate()
    {
        if (ProxyUrl.Length > 0) JableHttpClient.ValidateProxyUri(ProxyUrl);
        if (BrowserBridgeUrl.Length > 0)
        {
            JableHttpClient.ValidateBridgeUri(BrowserBridgeUrl);
            if (string.IsNullOrEmpty(BrowserBridgeToken) && string.IsNullOrEmpty(BridgeTokenInput))
                throw new ArgumentException("Browser bridge token is required when a bridge URL is configured.");
        }
        if (RecentPageCount is < 1 or > 100 || RequestTimeoutSeconds is < 5 or > 60 || MinimumRequestIntervalMs < 250)
            throw new ArgumentException("Recent pages must be 1–100, timeout 5–60 seconds, and request interval at least 250 ms.");
    }
}
