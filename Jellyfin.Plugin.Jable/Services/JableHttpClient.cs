using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable.Models;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.Jable.Services;

public sealed class JableHttpClient : IDisposable
{
    private const int MaxAttempts = 3;
    private const int MaxRedirects = 3;
    private const int MaxHtmlBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions BridgeJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly HttpClient _bridgeClient;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTimeOffset _nextRequestAt;

    public JableHttpClient()
        : this(
            BuildHandler(Plugin.Instance?.Configuration ?? new PluginConfiguration()),
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    public JableHttpClient(HttpMessageHandler handler, Func<PluginConfiguration> configuration, HttpMessageHandler? bridgeHandler = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(configuration);
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _bridgeClient = new HttpClient(bridgeHandler ?? BuildBridgeHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _configuration = configuration;
    }

    public static SocketsHttpHandler BuildBridgeHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
    };

    public static SocketsHttpHandler BuildHandler(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseProxy = false,
        };

        if (string.IsNullOrWhiteSpace(config.ProxyUrl))
        {
            return handler;
        }

        var proxyUri = ValidateProxyUri(config.ProxyUrl);
        var credentials = Credentials(proxyUri, config);
        var address = new UriBuilder(proxyUri) { UserName = string.Empty, Password = string.Empty }.Uri;
        handler.Proxy = new WebProxy(address) { Credentials = credentials };
        handler.UseProxy = true;
        return handler;
    }

    public static bool IsAllowedJableUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host.Equals("jable.tv", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".jable.tv", StringComparison.OrdinalIgnoreCase));

    public async Task<string> GetHtmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var scope = CreateRequestScope(cancellationToken);
        try
        {
            if (!IsAllowedJableUri(uri))
            {
                throw new JableRequestException(JableFailureKind.Network, "Jable URI is not allowed.");
            }

            if (!string.IsNullOrWhiteSpace(_configuration().BrowserBridgeUrl))
            {
                var html = await GetBridgeHtmlAsync(uri, scope.Token).ConfigureAwait(false);
                if (JableParser.IsChallengePage(html))
                {
                    throw new JableRequestException(JableFailureKind.Challenge, "Jable returned a challenge page.");
                }

                return html;
            }

            var result = await SendAsync(uri, inspectHtml: true, scope.Token).ConfigureAwait(false);
            using var response = result.Response;
            return result.Html!;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (scope.IsCancellationRequested)
        {
            throw new JableRequestException(JableFailureKind.Network, "Jable request timed out.", exception);
        }
        catch (JableRequestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or JsonException)
        {
            throw new JableRequestException(JableFailureKind.Network, "Jable response could not be read.", exception);
        }
    }

    public async Task<HttpResponseMessage> GetImageAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var scope = CreateRequestScope(cancellationToken);
        try
        {
            return (await SendAsync(uri, inspectHtml: false, scope.Token).ConfigureAwait(false)).Response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (scope.IsCancellationRequested)
        {
            throw new JableRequestException(JableFailureKind.Network, "Jable request timed out.", exception);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _bridgeClient.Dispose();
        _throttle.Dispose();
    }

    public async Task TestBridgeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configuration().BrowserBridgeUrl))
        {
            throw new JableRequestException(JableFailureKind.Network, "Jable browser bridge is not configured.");
        }

        _ = await GetHtmlAsync(new Uri("https://jable.tv/latest-updates/"), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetBridgeHtmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        var config = _configuration();
        var endpoint = new Uri(ValidateBridgeUri(config.BrowserBridgeUrl), "v1/render");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new BridgeRenderRequest(uri.AbsoluteUri))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.BrowserBridgeToken);
        using var response = await _bridgeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new JableRequestException(JableFailureKind.Network, $"Jable browser bridge returned {(int)response.StatusCode}.");
        }

        var json = await ReadHtmlAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var rendered = JsonSerializer.Deserialize<BridgeRenderResponse>(json, BridgeJsonOptions)
            ?? throw new JableRequestException(JableFailureKind.Network, "Jable browser bridge returned invalid JSON.");
        if (!Uri.TryCreate(rendered.Url, UriKind.Absolute, out var finalUri) || !IsAllowedJableUri(finalUri))
        {
            throw new JableRequestException(JableFailureKind.Network, "Jable browser bridge returned an unsafe URL.");
        }

        if (string.IsNullOrEmpty(rendered.Html))
        {
            throw new JableRequestException(JableFailureKind.Network, "Jable browser bridge returned empty HTML.");
        }

        if (Encoding.UTF8.GetByteCount(rendered.Html) > MaxHtmlBytes)
        {
            throw new JableRequestException(JableFailureKind.Network, "Jable HTML response is too large.");
        }

        return rendered.Html;
    }

    private async Task<JableResponse> SendAsync(Uri uri, bool inspectHtml, CancellationToken cancellationToken)
    {
        if (!IsAllowedJableUri(uri))
        {
            throw new JableRequestException(JableFailureKind.Network, "Jable URI is not allowed.");
        }

        var current = uri;
        var retries = 0;
        var redirects = 0;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                await ReserveRequestSlotAsync(cancellationToken).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (retries < MaxAttempts - 1)
            {
                await Task.Delay(RetryDelay(null, retries++), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException exception)
            {
                throw new JableRequestException(JableFailureKind.Network, "Jable request failed.", exception);
            }

            if (IsRedirect(response.StatusCode))
            {
                using (response)
                {
                    if (redirects++ >= MaxRedirects || response.Headers.Location is null || !Uri.TryCreate(current, response.Headers.Location, out var next) || !IsAllowedJableUri(next))
                    {
                        throw new JableRequestException(JableFailureKind.Network, "Jable redirect is not allowed.");
                    }

                    current = next;
                }

                continue;
            }

            string? html = null;
            if (inspectHtml)
            {
                try
                {
                    html = await ReadHtmlAsync(response.Content, cancellationToken).ConfigureAwait(false);
                    if (JableParser.IsChallengePage(html))
                    {
                        throw new JableRequestException(JableFailureKind.Challenge, "Jable returned a challenge page.");
                    }
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            if (IsRetryable(response.StatusCode))
            {
                if (retries++ < MaxAttempts - 1)
                {
                    var delay = RetryDelay(response, retries - 1);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                response.Dispose();
                throw new JableRequestException(JableFailureKind.Network, "Jable request failed after retries.");
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new JableRequestException(JableFailureKind.Network, "Jable returned an unsuccessful status.");
            }

            return new JableResponse(response, html);
        }
    }

    private async Task ReserveRequestSlotAsync(CancellationToken cancellationToken)
    {
        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan delay;
        try
        {
            var now = DateTimeOffset.UtcNow;
            delay = _nextRequestAt > now ? _nextRequestAt - now : TimeSpan.Zero;
            _nextRequestAt = (delay > TimeSpan.Zero ? _nextRequestAt : now).AddMilliseconds(Math.Max(0, _configuration().MinimumRequestIntervalMs));
        }
        finally
        {
            _throttle.Release();
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private CancellationTokenSource CreateRequestScope(CancellationToken callerCancellationToken)
    {
        var config = _configuration();
        var scope = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        scope.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 5, 60)));
        return scope;
    }

    private static async Task<string> ReadHtmlAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxHtmlBytes)
        {
            throw new JableRequestException(JableFailureKind.Network, "Jable HTML response is too large.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var rented = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                var available = MaxHtmlBytes + 1 - buffer.Length;
                if (available <= 0)
                {
                    throw new JableRequestException(JableFailureKind.Network, "Jable HTML response is too large.");
                }

                var read = await stream.ReadAsync(rented.AsMemory(0, (int)Math.Min(rented.Length, available)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
                }

                buffer.Write(rented, 0, read);
                if (buffer.Length > MaxHtmlBytes)
                {
                    throw new JableRequestException(JableFailureKind.Network, "Jable HTML response is too large.");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static bool IsRetryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static TimeSpan RetryDelay(HttpResponseMessage? response, int attempt)
    {
        var retryAfter = response?.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(500 * (1 << attempt)));
        return delay > TimeSpan.Zero ? TimeSpan.FromMilliseconds(Math.Min(10_000, delay.TotalMilliseconds)) : TimeSpan.Zero;
    }

    public static Uri ValidateProxyUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Proxy URL must be an HTTP, HTTPS, or SOCKS5 authority.", nameof(value));
        }

        return uri;
    }

    public static Uri ValidateBridgeUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host)
            || HasAuthorityUserInfo(value)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Browser bridge URL must be an HTTP or HTTPS authority.", nameof(value));
        return uri;
    }

    private static bool HasAuthorityUserInfo(string value)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) return false;
        authorityStart += 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        return value.AsSpan(authorityStart, (authorityEnd < 0 ? value.Length : authorityEnd) - authorityStart).Contains('@');
    }

    private static NetworkCredential? Credentials(Uri proxyUri, PluginConfiguration config)
    {
        if (!string.IsNullOrEmpty(config.ProxyUsername))
        {
            return new NetworkCredential(config.ProxyUsername, config.ProxyPassword);
        }

        if (string.IsNullOrEmpty(proxyUri.UserInfo))
        {
            return null;
        }

        var separator = proxyUri.UserInfo.IndexOf(':');
        var user = separator < 0 ? proxyUri.UserInfo : proxyUri.UserInfo[..separator];
        var password = separator < 0 ? string.Empty : proxyUri.UserInfo[(separator + 1)..];
        return new NetworkCredential(Uri.UnescapeDataString(user), Uri.UnescapeDataString(password));
    }

    private sealed record JableResponse(HttpResponseMessage Response, string? Html);
    private sealed record BridgeRenderRequest(string Url);
    private sealed record BridgeRenderResponse(string Url, string Html);
}

public sealed class JableRequestException : Exception
{
    public JableRequestException(JableFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException) => Kind = kind;

    public JableFailureKind Kind { get; }
}
