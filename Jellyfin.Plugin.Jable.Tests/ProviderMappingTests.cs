using System.Net;
using System.Net.Http;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable.Models;
using Jellyfin.Plugin.Jable.Providers;
using Jellyfin.Plugin.Jable.ScheduledTasks;
using Jellyfin.Plugin.Jable.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class ProviderMappingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jable-provider-" + Guid.NewGuid());
    private readonly PluginConfiguration _config = new() { SelectedLibraryId = Guid.NewGuid(), RecentPageCount = 1, MinimumRequestIntervalMs = 0 };
    private readonly List<Uri> _requests = [];
    private Func<Uri, CancellationToken, Task<string>> _fetch = (_, _) => Task.FromResult(Fixture("detail.html"));
    private readonly CatalogStore _store;
    private readonly JableCatalogService _catalog;
    private readonly JableHttpClient _client;
    private readonly JableMovieProvider _movies;
    private readonly JableImageProvider _images;

    public ProviderMappingTests()
    {
        _store = new CatalogStore(_directory);
        _catalog = new((uri, token) => { _requests.Add(uri); return _fetch(uri, token); }, new JableParser(), _store, () => _config, TimeProvider.System);
        _client = new(new ImageHandler(_requests), () => _config);
        var access = new LibraryAccessService(() => _config, _ => null,
            item => item.Path?.StartsWith("/selected/", StringComparison.Ordinal) == true ? [new Folder { Id = _config.SelectedLibraryId }] : [],
            () => [new VirtualFolderInfo { ItemId = _config.SelectedLibraryId.ToString(), Locations = ["/selected"] }], _ => []);
        _movies = new(_catalog, _client, access);
        _images = new(_catalog, _client, access);
    }

    [Fact]
    public void MapCopiesMetadataWithoutCountsOrActressGenres()
    {
        var movie = JableMovieProvider.Map(new JableWork
        {
            Number = "SSIS-123", Title = "Example", ReleaseDate = new(2025, 3, 2, 0, 0, 0, TimeSpan.Zero),
            Duration = TimeSpan.FromMinutes(90), Actresses = ["演员甲"], Genres = ["中文字幕", "单体作品"],
            Studio = "Studio", ViewCount = 18000, FavoriteCount = 700,
        });
        Assert.Equal("SSIS-123 Example", movie.Name);
        Assert.Equal("Example", movie.OriginalTitle);
        Assert.Equal("SSIS-123", movie.ProviderIds["Jable"]);
        Assert.Equal("SSIS-123", movie.ForcedSortName);
        Assert.Equal("XXX", movie.OfficialRating);
        Assert.Equal(2025, movie.ProductionYear);
        Assert.Equal(new DateTime(2025, 3, 2, 0, 0, 0, DateTimeKind.Utc), movie.PremiereDate);
        Assert.Equal(TimeSpan.FromMinutes(90).Ticks, movie.RunTimeTicks);
        Assert.Equal(["中文字幕", "单体作品"], movie.Genres);
        Assert.Equal(["Studio"], movie.Studios);
        Assert.Empty(movie.Tags);
        Assert.Null(movie.CommunityRating);
    }

    [Fact]
    public void MapLeavesMissingFieldsEmpty()
    {
        var movie = JableMovieProvider.Map(new JableWork { Number = "SSIS-123", Title = "  " });
        Assert.Equal("SSIS-123", movie.Name);
        Assert.Null(movie.ProductionYear);
        Assert.Null(movie.PremiereDate);
        Assert.Null(movie.RunTimeTicks);
        Assert.Empty(movie.Studios);
        Assert.Empty(movie.Genres);
    }

    [Theory]
    [InlineData("SSIS-123 Example title", "SSIS-123 Example title")]
    [InlineData("ssis_123 Example title", "ssis_123 Example title")]
    [InlineData("SSIS-123", "SSIS-123")]
    [InlineData("SSIS 123 Example title", "SSIS 123 Example title")]
    [InlineData("SSIS-1234 Other work", "SSIS-123 SSIS-1234 Other work")]
    public void MapDoesNotDuplicateAnExactLeadingNumber(string title, string expected)
    {
        Assert.Equal(expected, JableMovieProvider.Map(new() { Number = "SSIS-123", Title = title }).Name);
    }

    [Fact]
    public async Task ExactMetadataAddsActorsAndMarksQueriedById()
    {
        var result = await _movies.GetMetadata(new MovieInfo { Name = "SSIS-123", Path = "/selected/movie.mp4" }, CancellationToken.None);
        Assert.True(result.HasMetadata);
        Assert.True(result.QueriedById);
        Assert.Equal("SSIS-123 Example title", result.Item.Name);
        Assert.Equal("SSIS-123", result.Item.ProviderIds["Jable"]);
        var person = Assert.Single(result.People);
        Assert.Equal("演员甲", person.Name);
        Assert.Equal(PersonKind.Actor, person.Type);
        Assert.DoesNotContain(person.Name, result.Item.Genres);
        Assert.Null(result.Item.CommunityRating);
        Assert.Equal("https://jable.tv/videos/ssis-123/", Assert.Single(_requests).AbsoluteUri);
    }

    [Theory]
    [InlineData("/outside/SSIS-123.mp4", "SSIS-123")]
    [InlineData("/selected-other/SSIS-123.mp4", "SSIS-123")]
    [InlineData(null, "SSIS-123")]
    [InlineData("/selected/movie.mp4", "No number")]
    public async Task AutomaticMetadataSkipsOutOfScopeOrMissingNumbers(string? path, string name)
    {
        var result = await _movies.GetMetadata(new MovieInfo { Path = path, Name = name }, CancellationToken.None);
        Assert.False(result.HasMetadata);
        Assert.False(result.QueriedById);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("challenge")]
    [InlineData("mismatch")]
    public async Task FailedExactLookupReturnsNoMetadataOrImages(string failure)
    {
        _fetch = (_, _) => failure == "network" ? throw new HttpRequestException("offline")
            : Task.FromResult(failure == "challenge" ? Fixture("challenge.html") : Fixture("detail.html").Replace("SSIS-123", "SSIS-999").Replace("ssis-123", "ssis-999"));
        var result = await _movies.GetMetadata(new MovieInfo { Name = "SSIS-123", Path = "/selected/movie.mp4" }, CancellationToken.None);
        Assert.False(result.HasMetadata);
        Assert.False(result.QueriedById);
        Assert.Empty(await _images.GetImages(new Movie { Name = "SSIS-123", Path = "/selected/movie.mp4" }, CancellationToken.None));
    }

    [Fact]
    public async Task SearchRoutesNumbersToExactAndTextToCandidates()
    {
        var exact = Assert.Single(await _movies.GetSearchResults(new MovieInfo { Name = "ssis_123" }, CancellationToken.None));
        Assert.Equal("SSIS-123", exact.ProviderIds["Jable"]);
        Assert.Equal("/videos/ssis-123/", Assert.Single(_requests).AbsolutePath);
        _requests.Clear();
        _fetch = (_, _) => Task.FromResult(Fixture("list.html"));
        var candidates = (await _movies.GetSearchResults(new MovieInfo { Name = "演员甲" }, CancellationToken.None)).ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.All(candidates, candidate => Assert.Equal("Jable", candidate.SearchProviderName));
        Assert.Equal("/search/演员甲/", Uri.UnescapeDataString(Assert.Single(_requests, uri => uri.AbsolutePath.StartsWith("/search/")).AbsolutePath));
        Assert.Equal(2, _requests.Count(uri => uri.AbsolutePath.StartsWith("/videos/")));
        _requests.Clear();
        Assert.False((await _movies.GetMetadata(new MovieInfo { Name = "演员甲", Path = "/selected/movie.mp4" }, CancellationToken.None)).HasMetadata);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("ssis_123", "ABP-456", "/selected/OTHER-999.mp4")]
    [InlineData("invalid", "SSIS-123", "/selected/OTHER-999.mp4")]
    [InlineData(null, "No number", "/selected/SSIS-123.mp4")]
    public async Task MetadataNumberUsesProviderIdThenNameThenPath(string? providerId, string name, string path)
    {
        var info = new MovieInfo { Name = name, Path = path };
        if (providerId is not null) info.ProviderIds["Jable"] = providerId;
        var result = await _movies.GetMetadata(info, CancellationToken.None);
        Assert.True(result.HasMetadata);
        Assert.Equal("SSIS-123", result.Item.ProviderIds["Jable"]);
        Assert.Equal("/videos/ssis-123/", Assert.Single(_requests).AbsolutePath);
    }

    [Theory]
    [InlineData("SSIS-123")]
    [InlineData("演员甲")]
    public async Task SearchReturnsEmptyOnRemoteFailure(string query)
    {
        _fetch = (_, _) => throw new JableRequestException(JableFailureKind.Network, "offline");
        Assert.Empty(await _movies.GetSearchResults(new MovieInfo { Name = query }, CancellationToken.None));
    }

    [Theory]
    [InlineData("ssis_123", "ABP-456", "/selected/OTHER-999.mp4")]
    [InlineData("invalid", "SSIS-123", "/selected/OTHER-999.mp4")]
    [InlineData(null, "No number", "/selected/SSIS-123.mp4")]
    public async Task ImageNumberUsesProviderIdThenNameThenPath(string? providerId, string name, string path)
    {
        var movie = new Movie { Name = name, Path = path };
        if (providerId is not null) movie.ProviderIds["Jable"] = providerId;
        var image = Assert.Single(await _images.GetImages(movie, CancellationToken.None));
        Assert.Equal(ImageType.Primary, image.Type);
        Assert.Equal("https://assets.jable.tv/contents/videos/SSIS-123/cover.jpg", image.Url);
        Assert.Equal("/videos/ssis-123/", Assert.Single(_requests).AbsolutePath);
    }

    [Fact]
    public async Task ImagesSupportOnlySelectedMoviesAndPrimary()
    {
        var movie = new Movie { Name = "SSIS-123", Path = "/selected/movie.mp4" };
        Assert.True(_images.Supports(movie));
        Assert.Equal([ImageType.Primary], _images.GetSupportedImages(movie));
        Assert.False(_images.Supports(new Folder()));
        Assert.Empty(_images.GetSupportedImages(new Folder()));
        Assert.Empty(await _images.GetImages(new Folder { Name = "SSIS-123", Path = movie.Path }, CancellationToken.None));
        Assert.Empty(await _images.GetImages(new Movie { Name = "SSIS-123", Path = "/outside/movie.mp4" }, CancellationToken.None));
        Assert.Empty(await _images.GetImages(new Movie { Name = "No number", Path = "/selected/movie.mp4" }, CancellationToken.None));
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("http://assets.jable.tv/cover.jpg")]
    [InlineData("https://jable.tv.attacker.invalid/cover.jpg")]
    [InlineData("https://user@assets.jable.tv/cover.jpg")]
    [InlineData("/relative.jpg")]
    public async Task ImageUrlsAreRejectedAtDiscoveryAndDownload(string url)
    {
        _fetch = (_, _) => Task.FromResult(Fixture("detail.html").Replace("https://assets.jable.tv/contents/videos/SSIS-123/cover.jpg", url));
        // Seed a legacy unsafe poster too: catalog merge preserves a previous poster when a fresh detail has none.
        await _store.ReplaceAsync(new CatalogSnapshot { Works = new() { ["SSIS-123"] = new() { Work = new() { Number = "SSIS-123", Title = "Example", CanonicalUrl = "https://jable.tv/videos/ssis-123/", PosterUrl = url } } } }, CancellationToken.None);
        Assert.Empty(await _images.GetImages(new Movie { Name = "SSIS-123", Path = "/selected/movie.mp4" }, CancellationToken.None));
        _requests.Clear();
        await Assert.ThrowsAsync<JableRequestException>(() => _images.GetImageResponse(url, CancellationToken.None));
        await Assert.ThrowsAsync<JableRequestException>(() => _movies.GetImageResponse(url, CancellationToken.None));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task ImageDownloadDelegatesToNetworkClient()
    {
        using var response = await _images.GetImageResponse("https://assets.jable.tv/cover.jpg", CancellationToken.None);
        Assert.Equal("image", await response.Content.ReadAsStringAsync());
        Assert.Equal("https://assets.jable.tv/cover.jpg", Assert.Single(_requests).AbsoluteUri);
    }

    [Fact]
    public async Task ScheduledSyncUsesTwelveHourTriggerAndUpdatesCatalog()
    {
        _fetch = (_, _) => Task.FromResult(Fixture("list.html"));
        var task = new JableCatalogSyncTask(_catalog);
        Assert.Equal("JableCatalogSync", task.Key);
        Assert.Equal("Jable", task.Category);
        var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(TaskTriggerInfo.TriggerInterval, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(12).Ticks, trigger.IntervalTicks);
        var progress = new InlineProgress();
        await task.ExecuteAsync(progress, CancellationToken.None);
        Assert.Equal(100, progress.Last);
        Assert.Equal(2, _store.Snapshot.Works.Count);
        Assert.NotNull(_store.Snapshot.LastSuccessfulSync);
    }

    [Fact]
    public async Task CallerCancellationPropagatesFromEveryAsyncEntryPoint()
    {
        using var cancellation = new CancellationTokenSource();
        _fetch = (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(""); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _movies.GetMetadata(new MovieInfo { Name = "SSIS-123", Path = "/selected/movie.mp4" }, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _movies.GetSearchResults(new MovieInfo { Name = "演员甲" }, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _images.GetImages(new Movie { Name = "SSIS-123", Path = "/selected/movie.mp4" }, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _images.GetImageResponse("https://assets.jable.tv/cover.jpg", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JableCatalogSyncTask(_catalog).ExecuteAsync(new InlineProgress(), cancellation.Token));
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    public void Dispose() { _client.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class InlineProgress : IProgress<double>
    {
        public double Last { get; private set; }
        public void Report(double value) => Last = value;
    }
    private sealed class ImageHandler(List<Uri> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("image") });
        }
    }
}
