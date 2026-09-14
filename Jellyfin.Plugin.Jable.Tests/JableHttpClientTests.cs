using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable.Models;
using Jellyfin.Plugin.Jable.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class JableHttpClientTests
{
    private const int MaxHtmlBytes = 4 * 1024 * 1024;

    [Theory]
    [InlineData("http://jable.tv/videos/ssis-123/", false)]
    [InlineData("https://user:pass@jable.tv/videos/ssis-123/", false)]
    [InlineData("https://jable.tv.evil.test/videos/ssis-123/", false)]
    [InlineData("https://elsewhere.test/videos/ssis-123/", false)]
    [InlineData("https://jable.tv/videos/ssis-123/", true)]
    [InlineData("https://assets.jable.tv/cover.jpg", true)]
    public void HostValidationFailsClosed(string value, bool expected) =>
        Assert.Equal(expected, JableHttpClient.IsAllowedJableUri(new Uri(value)));

    [Fact]
    public void ValidateBridgeUriRejectsEmptyUserInfoDelimiter() =>
        Assert.Throws<ArgumentException>(() => JableHttpClient.ValidateBridgeUri("http://@bridge:3000/"));

    [Fact]
    public async Task GetHtmlFollowsThreeValidatedRedirects()
    {
        var handler = new QueueHandler(
            () => Redirect("https://assets.jable.tv/one"),
            () => Redirect("https://jable.tv/two"),
            () => Redirect("https://cdn.jable.tv/three"),
            () => Html("ok"));
        using var client = CreateClient(handler);

        var html = await client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None);

        Assert.Equal("ok", html);
        Assert.Equal(
            ["https://jable.tv/videos/ssis-123/", "https://assets.jable.tv/one", "https://jable.tv/two", "https://cdn.jable.tv/three"],
            handler.RequestUris.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task GetHtmlRejectsRedirectOutsideJableBeforeFollowingIt()
    {
        var handler = new QueueHandler(() => Redirect("https://jable.tv.evil.test/"));
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<JableRequestException>(() =>
            client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None));

        Assert.Equal(JableFailureKind.Network, error.Kind);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetHtmlRejectsTheFourthRedirect()
    {
        var handler = new QueueHandler(
            () => Redirect("https://jable.tv/one"),
            () => Redirect("https://jable.tv/two"),
            () => Redirect("https://jable.tv/three"),
            () => Redirect("https://jable.tv/four"));
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<JableRequestException>(() =>
            client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None));

        Assert.Equal(JableFailureKind.Network, error.Kind);
        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetHtmlRetries429And503WithinThreeTotalAttempts()
    {
        var handler = new QueueHandler(
            () => Status(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero),
            () => Status(HttpStatusCode.TooManyRequests, TimeSpan.Zero),
            () => Html("recovered"));
        using var client = CreateClient(handler);

        var html = await client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None);

        Assert.Equal("recovered", html);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetHtmlHonorsRetryAfter()
    {
        var handler = new QueueHandler(
            () => Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(900)),
            () => Html("recovered"));
        using var client = CreateClient(handler);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _ = await client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public async Task GetHtmlStopsAfterThreeTotalRetryAttempts()
    {
        var handler = new QueueHandler(
            () => Status(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero),
            () => Status(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero),
            () => Status(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero));
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<JableRequestException>(() =>
            client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None));

        Assert.Equal(JableFailureKind.Network, error.Kind);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetHtmlClassifiesChallengePagesWithoutRetrying()
    {
        var handler = new QueueHandler(() => Html(Fixture("challenge.html")));
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<JableRequestException>(() =>
            client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None));

        Assert.Equal(JableFailureKind.Challenge, error.Kind);
        Assert.Single(handler.RequestUris);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetHtmlClassifiesChallengeFailuresBeforeRetrying(HttpStatusCode status)
    {
        var handler = new QueueHandler(() => Challenge(status));
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<JableRequestException>(() =>
            client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None));

        Assert.Equal(JableFailureKind.Challenge, error.Kind);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetHtmlHonorsFutureRetryAfterDate()
    {
        var handler = new QueueHandler(
            () => StatusAt(HttpStatusCode.TooManyRequests, DateTimeOffset.UtcNow.AddSeconds(2)),
            () => Html("recovered"));
        using var client = CreateClient(handler);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _ = await client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task GetHtmlPropagatesCallerCancellation()
    {
        var handler = new NeverEndingHandler();
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();

        var request = client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task GetHtmlRejectsOversizedUnknownLengthContentWhileStreaming()
    {
        var stream = new OverflowAfterStream(MaxHtmlBytes + 1);
        var content = new StreamContent(stream);
        var handler = new QueueHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<JableRequestException>(() =>
            client.GetHtmlAsync(new Uri("https://jable.tv/videos/ssis-123/"), CancellationToken.None));

        Assert.Equal(JableFailureKind.Network, error.Kind);
        Assert.Contains("too large", error.Message);
        Assert.Equal(MaxHtmlBytes + 1, stream.BytesRead);
        Assert.False(stream.ReadPastEnd);
    }

    [Fact]
    public async Task GetImageReturnsHeadersWithoutBufferingTheBody()
    {
        var stream = new OverflowAfterStream(1);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var handler = new QueueHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = CreateClient(handler);

        using var response = await client.GetImageAsync(new Uri("https://assets.jable.tv/cover.jpg"), CancellationToken.None);

        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, stream.ReadCount);
    }

    [Fact]
    public void BuildHandlerUsesLegacyUrlCredentialsMigratedByPlugin()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, FinalRegressionTests.Proxy>();
        ((FinalRegressionTests.Proxy)(object)paths).Call = (_, _) => Path.GetTempPath();
        var serializer = DispatchProxy.Create<IXmlSerializer, FinalRegressionTests.Proxy>();
        ((FinalRegressionTests.Proxy)(object)serializer).Call = (method, _) => method.Name == "DeserializeFromFile"
            ? new PluginConfiguration { ProxyUrl = "http://user:pass@proxy.test:8080", ProxyPassword = "legacy-separate-password" } : null;
        var plugin = new Plugin(paths, serializer);
        using var handler = JableHttpClient.BuildHandler(plugin.Configuration);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        var credential = Assert.IsType<NetworkCredential>(proxy.Credentials);

        Assert.Equal("http://proxy.test:8080/", proxy.Address?.AbsoluteUri);
        Assert.Equal("user", credential.UserName);
        Assert.Equal("pass", credential.Password);
    }

    [Fact]
    public void BuildHandlerSupportsSocks5AndSeparateCredentials()
    {
        using var handler = JableHttpClient.BuildHandler(new PluginConfiguration
        {
            ProxyUrl = "socks5://proxy.test:1080",
            ProxyUsername = "user",
            ProxyPassword = "pass",
        });
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        var credential = Assert.IsType<NetworkCredential>(proxy.Credentials);

        Assert.Equal("socks5://proxy.test:1080/", proxy.Address?.AbsoluteUri);
        Assert.Equal("user", credential.UserName);
        Assert.Equal("pass", credential.Password);
    }

    [Theory]
    [InlineData("ftp://proxy.test:21")]
    [InlineData("http://proxy.test:8080/path")]
    [InlineData("https://proxy.test:8443/?query")]
    [InlineData("socks5://proxy.test:1080/#fragment")]
    public void BuildHandlerRejectsProxyUrlsOutsideAuthorityForm(string proxyUrl) =>
        Assert.Throws<ArgumentException>(() => JableHttpClient.BuildHandler(new PluginConfiguration { ProxyUrl = proxyUrl }));

    private static JableHttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler, () => new PluginConfiguration { MinimumRequestIntervalMs = 0, RequestTimeoutSeconds = 60 });

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html) };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Status(HttpStatusCode status, TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private static HttpResponseMessage StatusAt(HttpStatusCode status, DateTimeOffset retryAfter)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private static HttpResponseMessage Challenge(HttpStatusCode status) =>
        new(status) { Content = new StringContent(Fixture("challenge.html")) };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class QueueHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new(responses);

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(_responses.Dequeue().Invoke());
        }
    }

    private sealed class NeverEndingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class OverflowAfterStream(long readableBytes) : Stream
    {
        private long _remaining = readableBytes;

        public int ReadCount { get; private set; }
        public long BytesRead { get; private set; }
        public bool ReadPastEnd { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (_remaining == 0)
            {
                ReadPastEnd = true;
                throw new InvalidOperationException("The response was read past the permitted buffer.");
            }

            var count = (int)Math.Min(buffer.Length, _remaining);
            _remaining -= count;
            BytesRead += count;
            buffer[..count].Span.Fill((byte)'x');
            return ValueTask.FromResult(count);
        }
    }
}
