using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.Jable.Api;
using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable.Models;
using Jellyfin.Plugin.Jable.ScheduledTasks;
using Jellyfin.Plugin.Jable.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class JableAuthorizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jable-api-" + Guid.NewGuid());
    private readonly PluginConfiguration _config = new() { SelectedLibraryId = Guid.NewGuid(), MinimumRequestIntervalMs = 0 };
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly UserPolicy _policy = new() { EnableAllFolders = true };
    private readonly CatalogStore _store;
    private readonly JableHttpClient _client;
    private readonly JableController _controller;
    private readonly List<Uri> _imageRequests = [];
    private readonly List<Type> _queuedTasks = [];
    private Func<HttpResponseMessage> _imageResponse = () => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
    private bool _userExists = true;
    private int _htmlRequests;

    public JableAuthorizationTests()
    {
        _store = new CatalogStore(_directory);
        var catalog = new JableCatalogService((_, _) =>
        {
            _htmlRequests++;
            return Task.FromResult(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "detail.html")));
        }, new JableParser(), _store, () => _config, TimeProvider.System);
        var users = DispatchProxy.Create<IUserManager, InterfaceProxy>();
        ((InterfaceProxy)(object)users).Call = (method, args) => method.Name switch
        {
            "GetUserById" => _userExists && (Guid)args![0]! == _user.Id ? _user : null,
            "GetUserDto" => new UserDto { Policy = _policy },
            _ => throw new InvalidOperationException(method.Name),
        };
        var tasks = DispatchProxy.Create<ITaskManager, InterfaceProxy>();
        ((InterfaceProxy)(object)tasks).Call = (method, _) =>
        {
            Assert.Equal("QueueIfNotRunning", method.Name);
            _queuedTasks.Add(Assert.Single(method.GetGenericArguments()));
            return null;
        };
        var access = new LibraryAccessService(() => _config, _ => _policy, _ => [], () => [], _ =>
            [new Movie { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Name = "SSIS-123" }]);
        _client = new JableHttpClient(new ImageHandler(request =>
        {
            Assert.False(request.Headers.Contains("X-Emby-Token"));
            _imageRequests.Add(request.RequestUri!);
            return _imageResponse();
        }), () => _config);
        _controller = new JableController(catalog, access, users, tasks, _client)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(_user.Id.ToString("N")) } },
        };
    }

    [Fact]
    public void UserIdReadsJellyfinClaim() => Assert.Equal(_user.Id, JableAuthorization.GetUserId(Principal(_user.Id.ToString("N"))));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000000000000000000000000000")]
    public void MissingOrInvalidClaimFailsClosed(string? claim) => Assert.Null(JableAuthorization.GetUserId(Principal(claim)));

    [Fact]
    public void UnauthenticatedOrUnrelatedClaimFailsClosed()
    {
        Assert.Null(JableAuthorization.GetUserId(new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", _user.Id.ToString())]))));
        Assert.Null(JableAuthorization.GetUserId(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _user.Id.ToString())], "test"))));
    }

    [Fact]
    public void AdministratorRequiresExplicitCurrentPolicy()
    {
        Assert.False(JableAuthorization.IsAdministrator(null));
        Assert.False(JableAuthorization.IsAdministrator(new UserPolicy { EnableAllFolders = true }));
        Assert.True(JableAuthorization.IsAdministrator(new UserPolicy { IsAdministrator = true }));
    }

    [Fact]
    public void OnlyStaticResourcesAllowAnonymous()
    {
        Assert.NotNull(typeof(JableController).GetCustomAttribute<AuthorizeAttribute>());
        foreach (var name in new[] { "GetPage", "GetAsset" })
            Assert.NotNull(typeof(JableController).GetMethod(name)!.GetCustomAttribute<AllowAnonymousAttribute>());
        foreach (var name in new[] { "GetCatalog", "GetWork", "GetImage", "GetStatus", "Sync", "ClearSearch" })
            Assert.Null(typeof(JableController).GetMethod(name)!.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Theory]
    [InlineData("missing", 401)]
    [InlineData("invalid", 401)]
    [InlineData("deleted", 401)]
    [InlineData("no-library", 403)]
    [InlineData("blocked", 403)]
    public async Task EveryDataEndpointRejectsUnauthorizedUsersBeforeAnyDataAccess(string reason, int status)
    {
        if (reason == "missing") _controller.HttpContext.User = Principal(null);
        if (reason == "invalid") _controller.HttpContext.User = Principal("invalid");
        if (reason == "deleted") _userExists = false;
        if (reason == "no-library") _config.SelectedLibraryId = Guid.Empty;
        if (reason == "blocked") _policy.BlockedMediaFolders = [_config.SelectedLibraryId];
        foreach (var operation in DataEndpoints())
        {
            var result = await operation();
            if (status == 401) Assert.IsType<UnauthorizedResult>(result);
            else Assert.IsType<ForbidResult>(result);
        }
        Assert.Equal(0, _htmlRequests);
        Assert.Empty(_imageRequests);
        Assert.Empty(_queuedTasks);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task OrdinaryUserReadsSelectedCatalogButCannotSyncOrClear()
    {
        await Seed();
        var page = Assert.IsType<CatalogPageDto>(Assert.IsType<OkObjectResult>(await _controller.GetCatalog(new(), CancellationToken.None)).Value);
        var item = Assert.Single(page.Items);
        Assert.True(item.IsLocal);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), item.JellyfinItemId);
        Assert.IsType<OkObjectResult>(await _controller.GetStatus(CancellationToken.None));
        var work = Assert.IsType<CatalogItemDto>(Assert.IsType<OkObjectResult>(await _controller.GetWork("SSIS-123", CancellationToken.None)).Value);
        Assert.Equal(1, page.ApiVersion);
        Assert.Equal(1, work.ApiVersion);
        var json = System.Text.Json.JsonSerializer.Serialize(work);
        Assert.DoesNotContain("PosterUrl", json);
        Assert.DoesNotContain("SourcePage", json);
        Assert.IsType<ForbidResult>(_controller.Sync());
        Assert.IsType<ForbidResult>(await _controller.ClearSearch(CancellationToken.None));
        Assert.Single(_store.Snapshot.Works);
        Assert.Empty(_queuedTasks);
    }

    [Fact]
    public async Task AdministratorQueuesSyncAndClearsSearchWithoutFetching()
    {
        _policy.IsAdministrator = true;
        await Seed();
        Assert.IsType<AcceptedResult>(_controller.Sync());
        Assert.Equal(typeof(JableCatalogSyncTask), Assert.Single(_queuedTasks));
        Assert.IsType<NoContentResult>(await _controller.ClearSearch(CancellationToken.None));
        Assert.Empty(_store.Snapshot.Works);
        Assert.Equal(0, _htmlRequests);
    }

    [Fact]
    public void StaticResourcesAreEmbeddedAndNamesAreAllowlisted()
    {
        var page = Assert.IsType<FileStreamResult>(_controller.GetPage());
        using (page.FileStream) Assert.Contains("Jable/Assets/jable.mjs", new StreamReader(page.FileStream).ReadToEnd());
        foreach (var name in new[] { "jable.css", "jable.mjs" })
        {
            var result = Assert.IsType<FileStreamResult>(_controller.GetAsset(name));
            using (result.FileStream) Assert.True(result.FileStream.Length > 0);
        }
        foreach (var name in new[] { "jable.html", "../Plugin.cs", "Jable.mjs", "jable.mjs/extra", "" })
            Assert.IsType<NotFoundResult>(_controller.GetAsset(name));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public async Task ImageUsesOnlyCachedPosterAndPrivateCache(string mime)
    {
        await Seed();
        var stream = new TrackingStream([1, 2, 3]);
        _imageResponse = () => ImageContent(stream, mime);
        var result = Assert.IsType<FileContentResult>(await _controller.GetImage("ssis_123", CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, result.FileContents);
        Assert.Equal(mime, result.ContentType);
        Assert.Equal("private, max-age=86400", _controller.Response.Headers.CacheControl);
        Assert.True(stream.Disposed);
        Assert.Equal("https://assets.jable.tv/cover.jpg", Assert.Single(_imageRequests).AbsoluteUri);
        Assert.Equal(0, _htmlRequests);
    }

    [Theory]
    [InlineData("http://assets.jable.tv/cover.jpg")]
    [InlineData("https://jable.tv.attacker.invalid/cover.jpg")]
    [InlineData("https://user@assets.jable.tv/cover.jpg")]
    [InlineData("/cover.jpg")]
    public async Task ImageRejectsUntrustedCachedUrlWithoutFetching(string url)
    {
        await Seed(url);
        Assert.IsType<NotFoundResult>(await _controller.GetImage("SSIS-123", CancellationToken.None));
        Assert.Empty(_imageRequests);
        Assert.Equal(0, _htmlRequests);
    }

    [Fact]
    public async Task ImageDoesNotFetchUncachedWorkOrCallerUrl()
    {
        foreach (var number in new[] { "SSIS-123", "https://assets.jable.tv/cover.jpg" })
            Assert.IsType<NotFoundResult>(await _controller.GetImage(number, CancellationToken.None));
        Assert.Empty(_imageRequests);
        Assert.Equal(0, _htmlRequests);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData(null)]
    public async Task ImageRejectsUnexpectedContentTypeWithoutReading(string? mime)
    {
        await Seed();
        var stream = new BoundedStream(0);
        _imageResponse = () => ImageContent(stream, mime);
        Assert.Equal(502, Assert.IsType<StatusCodeResult>(await _controller.GetImage("SSIS-123", CancellationToken.None)).StatusCode);
        Assert.True(stream.Disposed);
        Assert.Equal(0, stream.ReadBytes);
    }

    [Fact]
    public async Task ImageRejectsFailedStatusAndDisposesResponse()
    {
        await Seed();
        var stream = new BoundedStream(0);
        _imageResponse = () => { var response = ImageContent(stream, "image/png"); response.StatusCode = HttpStatusCode.Forbidden; return response; };
        Assert.Equal(502, Assert.IsType<StatusCodeResult>(await _controller.GetImage("SSIS-123", CancellationToken.None)).StatusCode);
        Assert.True(stream.Disposed);
        Assert.Equal(0, stream.ReadBytes);
    }

    [Fact]
    public async Task ImageStopsUnknownLengthResponseAtThirtyMiBPlusOne()
    {
        await Seed();
        var stream = new BoundedStream(30 * 1024 * 1024 + 1);
        _imageResponse = () => ImageContent(stream, "image/jpeg");
        Assert.Equal(502, Assert.IsType<StatusCodeResult>(await _controller.GetImage("SSIS-123", CancellationToken.None)).StatusCode);
        Assert.Equal(30 * 1024 * 1024 + 1, stream.ReadBytes);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ImageRejectsDeclaredOversizeWithoutReading()
    {
        await Seed();
        var stream = new BoundedStream(0);
        _imageResponse = () =>
        {
            var response = ImageContent(stream, "image/png");
            response.Content.Headers.ContentLength = 30 * 1024 * 1024 + 1;
            return response;
        };
        Assert.Equal(502, Assert.IsType<StatusCodeResult>(await _controller.GetImage("SSIS-123", CancellationToken.None)).StatusCode);
        Assert.Equal(0, stream.ReadBytes);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ImageAcceptsExactlyThirtyMiBAndVariesPrivateCacheByToken()
    {
        await Seed();
        var stream = new TrackingStream(new byte[30 * 1024 * 1024]);
        _imageResponse = () => ImageContent(stream, "image/webp");
        var result = Assert.IsType<FileContentResult>(await _controller.GetImage("SSIS-123", CancellationToken.None));
        Assert.Equal(30 * 1024 * 1024, result.FileContents.Length);
        Assert.Equal("X-Emby-Token", _controller.Response.Headers.Vary);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ImageDoesNotTrustAnUnderreportedContentLength()
    {
        await Seed();
        var stream = new BoundedStream(30 * 1024 * 1024 + 1);
        _imageResponse = () =>
        {
            var response = ImageContent(stream, "image/jpeg");
            response.Content.Headers.ContentLength = 1;
            return response;
        };
        Assert.Equal(502, Assert.IsType<StatusCodeResult>(await _controller.GetImage("SSIS-123", CancellationToken.None)).StatusCode);
        Assert.Equal(30 * 1024 * 1024 + 1, stream.ReadBytes);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ImagePropagatesCallerCancellationAndDisposesBody()
    {
        await Seed();
        using var cancellation = new CancellationTokenSource();
        var stream = new CancellingStream(cancellation);
        _imageResponse = () => ImageContent(stream, "image/jpeg");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _controller.GetImage("SSIS-123", cancellation.Token));
        Assert.True(stream.Disposed);
    }

    private Func<Task<IActionResult>>[] DataEndpoints() =>
    [
        () => _controller.GetCatalog(new() { Search = "SSIS-123" }, CancellationToken.None),
        () => _controller.GetWork("SSIS-123", CancellationToken.None),
        () => _controller.GetImage("SSIS-123", CancellationToken.None),
        () => _controller.GetStatus(CancellationToken.None),
        () => Task.FromResult(_controller.Sync()),
        () => _controller.ClearSearch(CancellationToken.None),
    ];

    private Task Seed(string poster = "https://assets.jable.tv/cover.jpg") => _store.ReplaceAsync(new CatalogSnapshot
    {
        Works = new() { ["SSIS-123"] = new() { Work = new() { Number = "SSIS-123", Title = "Example", PosterUrl = poster, CanonicalUrl = "https://jable.tv/videos/ssis-123/" }, SearchExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } },
    }, CancellationToken.None);

    private static ClaimsPrincipal Principal(string? claim) => new(new ClaimsIdentity(claim is null ? [] : [new Claim("Jellyfin-UserId", claim)], "test"));
    private static HttpResponseMessage ImageContent(Stream stream, string? mime)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (mime is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return response;
    }
    public void Dispose() { _client.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
    private sealed class ImageHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class CancellingStream(CancellationTokenSource cancellation) : TrackingStream([1])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }
    private sealed class BoundedStream(int allowedBytes) : Stream
    {
        public long ReadBytes { get; private set; }
        public bool Disposed { get; private set; }
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
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadBytes >= allowedBytes) throw new InvalidOperationException("Body read beyond the permitted limit.");
            var count = (int)Math.Min(buffer.Length, allowedBytes - ReadBytes);
            buffer.Span[..count].Fill(1);
            ReadBytes += count;
            return ValueTask.FromResult(count);
        }
    }
}
