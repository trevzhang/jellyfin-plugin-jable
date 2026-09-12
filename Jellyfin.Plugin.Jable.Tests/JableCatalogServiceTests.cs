using System.Net.Http;
using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable.Models;
using Jellyfin.Plugin.Jable.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class JableCatalogServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jable-catalog-" + Guid.NewGuid());
    private readonly PluginConfiguration _config = new() { SelectedLibraryId = Guid.NewGuid(), RecentPageCount = 2 };
    private readonly TestClock _clock = new();
    private readonly CatalogStore _store;
    private readonly List<Uri> _requests = [];
    private Func<Uri, CancellationToken, Task<string>> _fetch = (_, _) => Task.FromResult(Fixture("list.html"));
    private readonly JableCatalogService _service;

    public JableCatalogServiceTests()
    {
        _store = new CatalogStore(_directory);
        _service = new JableCatalogService((uri, token) => { _requests.Add(uri); return _fetch(uri, token); }, new JableParser(), _store, () => _config, _clock);
    }

    [Fact]
    public async Task RecentSyncFollowsNextAndMergesWithoutErasingDetail()
    {
        await Seed(new JableWork { Number = "SSIS-123", Actresses = ["Existing"], Studio = "Studio", ReleaseDate = _clock.Now.AddYears(-1), ViewCount = 1 });
        var progress = new InlineProgress();
        _fetch = (uri, _) => Task.FromResult(uri.AbsolutePath.EndsWith("/2/", StringComparison.Ordinal)
            ? Card("ssis-123", "Updated", "3M 20K") : Fixture("list.html"));

        await _service.SyncRecentAsync(progress, CancellationToken.None);

        Assert.Equal(["https://jable.tv/latest-updates/", "https://jable.tv/latest-updates/2/"], _requests.Where(uri => !uri.AbsolutePath.StartsWith("/videos/")).Select(x => x.AbsoluteUri));
        Assert.Equal(2, _requests.Count(uri => uri.AbsolutePath.StartsWith("/videos/")));
        Assert.Equal(100, progress.Values.Last());
        Assert.Equal(2, _store.Snapshot.Works.Count);
        var entry = _store.Snapshot.Works["SSIS-123"];
        Assert.True(entry.InRecent);
        Assert.Equal("ssis-123 Updated", entry.Work.Title);
        Assert.Equal(3_000_000, entry.Work.ViewCount);
        Assert.Equal(["Existing"], entry.Work.Actresses);
        Assert.Equal("Studio", entry.Work.Studio);
        Assert.Equal(_clock.Now, (await _service.GetStatusAsync(CancellationToken.None)).LastSuccessfulSync);
    }

    [Fact]
    public async Task RecentFailurePreservesWholePreviousSetAndReportsError()
    {
        await Seed(new JableWork { Number = "OLD-001", Title = "Previous" });
        _fetch = (uri, _) => uri.AbsolutePath.EndsWith("/2/", StringComparison.Ordinal)
            ? throw new HttpRequestException("offline") : Task.FromResult(Fixture("list.html"));

        await _service.SyncRecentAsync(new InlineProgress(), CancellationToken.None);

        Assert.Equal("OLD-001", Assert.Single(_store.Snapshot.Works).Key);
        Assert.True(_store.Snapshot.Works["OLD-001"].InRecent);
        var status = await _service.GetStatusAsync(CancellationToken.None);
        Assert.Null(status.LastSuccessfulSync);
        Assert.Contains("offline", status.LastError);
    }

    [Fact]
    public async Task RecentCommitRemovesOldEntriesButKeepsUnexpiredSearch()
    {
        await Seed(new JableWork { Number = "OLD-001" });
        await _store.MutateAsync(snapshot =>
        {
            snapshot.Works["KEEP-001"] = new() { Work = Complete(new() { Number = "KEEP-001" }), SearchExpiresAt = _clock.Now.AddHours(1) };
            snapshot.Works["GONE-001"] = new() { Work = Complete(new() { Number = "GONE-001" }), SearchExpiresAt = _clock.Now };
            snapshot.SearchCache["expired"] = _clock.Now;
            return snapshot;
        }, CancellationToken.None);
        _config.RecentPageCount = 1;
        await _service.SyncRecentAsync(new InlineProgress(), CancellationToken.None);
        Assert.Equal(["ABP-456", "KEEP-001", "SSIS-123"], _store.Snapshot.Works.Keys.Order());
        Assert.Empty(_store.Snapshot.SearchCache);
    }

    [Fact]
    public async Task RecentNeverFetchesUntrustedNextLink()
    {
        _fetch = (_, _) => Task.FromResult(Fixture("list.html").Replace("https://jable.tv/latest-updates/2/", "https://attacker.invalid/"));
        await _service.SyncRecentAsync(new InlineProgress(), CancellationToken.None);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task UnparseableRecentPageDoesNotClearCatalog()
    {
        await Seed(new JableWork { Number = "OLD-001" });
        _fetch = (_, _) => Task.FromResult("<html>Temporarily unavailable</html>");
        await _service.SyncRecentAsync(new InlineProgress(), CancellationToken.None);
        Assert.Equal("OLD-001", Assert.Single(_store.Snapshot.Works).Key);
        Assert.NotEmpty((await _service.GetStatusAsync(CancellationToken.None)).LastError);
    }

    [Fact]
    public async Task MissingLibraryPreventsEveryRemoteOperation()
    {
        _config.SelectedLibraryId = Guid.Empty;
        await _service.SyncRecentAsync(new InlineProgress(), CancellationToken.None);
        Assert.Null(await _service.GetExactAsync("SSIS-123", CancellationToken.None));
        Assert.Empty(await _service.SearchRemoteAsync("actress", CancellationToken.None));
        await _service.QueryAsync(new() { Search = "SSIS-123" }, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Empty(_requests);
        Assert.Empty(_store.Snapshot.SearchCache);
    }

    [Theory]
    [InlineData("ssis_123", "https://jable.tv/videos/ssis-123/", "SSIS-123")]
    [InlineData("1pondo-123456_789", "https://jable.tv/videos/1pondo-123456-789/", "1PONDO-123456_789")]
    public async Task ExactLookupUsesNormalizedSingleSegment(string number, string url, string expected)
    {
        _fetch = (_, _) => Task.FromResult($"<title>{expected} Detail - Jable.TV</title><link rel='canonical' href='{url}'>");
        var work = await _service.GetExactAsync(number, CancellationToken.None);
        Assert.Equal(url, Assert.Single(_requests).AbsoluteUri);
        Assert.Equal(expected, work?.Number);
        Assert.Equal(_clock.Now.AddHours(24), _store.Snapshot.Works[expected].SearchExpiresAt);
    }

    [Fact]
    public async Task ExactRejectsMismatchWithoutCachingForeignWork()
    {
        _fetch = (_, _) => Task.FromResult(Fixture("detail.html"));
        Assert.Null(await _service.GetExactAsync("SSIS-124", CancellationToken.None));
        Assert.Empty(_store.Snapshot.Works);
        Assert.NotEmpty((await _service.GetStatusAsync(CancellationToken.None)).LastError);
    }

    [Fact]
    public async Task DetailMergeAddsMetadataAndPreservesMissingCountsAndFields()
    {
        await Seed(new JableWork
        {
            Number = "SSIS-123", Title = "Old", Actresses = ["Old"], Genres = ["Old"],
            ViewCount = 200, FavoriteCount = 30, Studio = "Existing studio", Duration = TimeSpan.FromMinutes(90),
            ReleaseDate = _clock.Now.AddYears(-1), CanonicalUrl = "https://jable.tv/videos/SSIS-123/",
        });
        _fetch = (_, _) => Task.FromResult(Fixture("detail.html"));
        var work = await _service.GetExactAsync("SSIS-123", CancellationToken.None);
        Assert.NotNull(work);
        Assert.Equal(["演员甲"], work.Actresses);
        Assert.Equal(["中文字幕", "单体作品"], work.Genres);
        Assert.Equal(_clock.Now.AddDays(-3), work.ReleaseDate);
        Assert.Equal("https://jable.tv/videos/ssis-123/", work.CanonicalUrl);
        Assert.Equal("https://assets.jable.tv/contents/videos/SSIS-123/cover.jpg", work.PosterUrl);
        Assert.Equal(200, work.ViewCount);
        Assert.Equal(30, work.FavoriteCount);
        Assert.Equal("Example Studio", work.Studio);
        Assert.Equal(TimeSpan.FromMinutes(120), work.Duration);
        Assert.True(_store.Snapshot.Works["SSIS-123"].InRecent);
    }

    [Fact]
    public async Task CachedLookupLoadsDiskAndNeverFetches()
    {
        await Seed(new JableWork { Number = "SSIS-123", Title = "Cached" });
        var service = new JableCatalogService((_, _) => throw new Exception("HTTP forbidden"), new JableParser(), new CatalogStore(_directory), () => _config, _clock);
        Assert.Equal("Cached", (await service.GetCachedAsync("ssis_123", CancellationToken.None))?.Title);
        Assert.Null(await service.GetCachedAsync("ABP-456", CancellationToken.None));
    }

    [Fact]
    public async Task TextSearchEscapesRawTrimmedQueryAndExpiresAt24Hours()
    {
        await _service.SearchRemoteAsync("  actress / 中文?#  ", CancellationToken.None);
        Assert.Equal("https://jable.tv/search/actress%20%2F%20%E4%B8%AD%E6%96%87%3F%23/", Assert.Single(SearchRequests).AbsoluteUri);
        Assert.Equal(_clock.Now.AddHours(24), _store.Snapshot.SearchCache["ACTRESS / 中文?#"]);
        _clock.Now = _clock.Now.AddHours(24);
        Assert.Empty((await _service.QueryAsync(new(), new Dictionary<string, Guid>(), CancellationToken.None)).Items);
    }

    [Theory]
    [InlineData("Example", "/search/Example/")]
    [InlineData("ssis-123", "/videos/ssis-123/")]
    public async Task FirstPageRefreshesOnlyAbsentOrExpiredTerm(string term, string path)
    {
        _fetch = (uri, _) => Task.FromResult(uri.AbsolutePath.StartsWith("/videos/", StringComparison.Ordinal) ? Fixture("detail.html") : Fixture("list.html"));
        await Query(term);
        await Query(term.ToUpperInvariant());
        Assert.Equal(path, _requests[0].AbsolutePath);
        var perRefresh = term == "Example" ? 3 : 1;
        Assert.Equal(perRefresh, _requests.Count);
        _clock.Now = _clock.Now.AddHours(24);
        await Query(term);
        Assert.Equal(perRefresh * 2, _requests.Count);
        await _service.QueryAsync(new() { Search = "other", StartIndex = 1 }, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Equal(perRefresh * 2, _requests.Count);
    }

    [Fact]
    public async Task FailedRefreshReturnsExpiredCachedMatchesAndRecordsErrorWithoutExtendingTtl()
    {
        await _service.SearchRemoteAsync("Example", CancellationToken.None);
        var expiry = _store.Snapshot.SearchCache["Example"];
        _clock.Now = expiry;
        _fetch = (_, _) => throw new HttpRequestException("offline");
        var result = await Query("Example");
        Assert.Equal(2, result.TotalRecordCount);
        Assert.Contains(result.Items, work => work.Number == "SSIS-123");
        Assert.Equal(expiry, _store.Snapshot.SearchCache["Example"]);
        Assert.Contains("offline", (await _service.GetStatusAsync(CancellationToken.None)).LastError);
    }

    [Fact]
    public async Task FailedRefreshKeepsExpiredResultsAcrossPagesUntilSuccessfulRefresh()
    {
        var expiry = _clock.Now.AddHours(-1);
        await _store.ReplaceAsync(new CatalogSnapshot
        {
            Works = Enumerable.Range(1, 60).ToDictionary(i => $"ABP-{i:000}", i => new CatalogEntry
            {
                Work = Complete(new JableWork { Number = $"ABP-{i:000}", Title = "Example Other" }), SearchExpiresAt = expiry,
            }),
            SearchCache = new() { ["Example"] = expiry },
        }, CancellationToken.None);
        _fetch = (_, _) => throw new HttpRequestException("offline");

        var first = await Query("Example");
        var second = await _service.QueryAsync(new() { Search = "EXAMPLE", StartIndex = 50 }, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Equal(60, first.TotalRecordCount);
        Assert.Equal(50, first.Items.Length);
        Assert.Equal(60, second.TotalRecordCount);
        Assert.Equal(10, second.Items.Length);
        Assert.Equal("ABP-051", second.Items[0].Number);
        Assert.Single(_requests);
        Assert.Equal(expiry, _store.Snapshot.SearchCache["Example"]);
        Assert.All(_store.Snapshot.Works.Values, entry => Assert.Equal(expiry, entry.SearchExpiresAt));
        var other = await _service.QueryAsync(new() { Search = "Other", StartIndex = 50 }, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Equal(0, other.TotalRecordCount);
        Assert.Single(_requests);

        _fetch = (_, _) => Task.FromResult(string.Concat(Enumerable.Range(1, 60).Select(i => Card($"abp-{i:000}", "Example", "10 1"))));
        await Query("example");
        Assert.Equal(2, SearchRequests.Count());
        _clock.Now = _clock.Now.AddHours(24);
        var afterSuccess = await _service.QueryAsync(new() { Search = "EXAMPLE", StartIndex = 50 }, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Equal(0, afterSuccess.TotalRecordCount);
        Assert.Equal(2, SearchRequests.Count());
    }

    [Theory]
    [InlineData(CatalogSourceFilter.All, 2)]
    [InlineData(CatalogSourceFilter.Local, 1)]
    [InlineData(CatalogSourceFilter.Online, 1)]
    public async Task QueryFiltersSourceAndMapsIdentityKeys(CatalogSourceFilter source, int count)
    {
        await Seed(new() { Number = "SSIS-123" }, new() { Number = "ABP-456" });
        var id = Guid.NewGuid();
        var page = await _service.QueryAsync(new() { Source = source }, new Dictionary<string, Guid> { ["SSIS123"] = id }, CancellationToken.None);
        Assert.Equal(count, page.TotalRecordCount);
        foreach (var item in page.Items)
        {
            Assert.Equal(item.Number == "SSIS-123", item.IsLocal);
            Assert.Equal(item.IsLocal ? id : (Guid?)null, item.JellyfinItemId);
            Assert.Equal("/Jable/Images/" + item.Number, item.ImagePath);
        }
    }

    [Fact]
    public async Task ConcurrentFirstPagesRefreshOnceAndCannotRestoreAStaleFailureMarker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var succeed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failLate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _fetch = async (uri, token) =>
        {
            if (uri.AbsolutePath.StartsWith("/videos/")) return "<html>No optional detail</html>";
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await succeed.Task.WaitAsync(token);
                return Fixture("list.html");
            }

            await failLate.Task.WaitAsync(token);
            throw new HttpRequestException("late failure");
        };

        var first = Query("Example");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Query("EXAMPLE");
        succeed.SetResult();
        var firstPage = await first.WaitAsync(TimeSpan.FromSeconds(5));
        failLate.SetResult();
        var secondPage = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal(2, firstPage.TotalRecordCount);
        Assert.Equal(2, secondPage.TotalRecordCount);
        Assert.Empty((await _service.GetStatusAsync(CancellationToken.None)).LastError);

        _clock.Now = _clock.Now.AddHours(24);
        var later = await _service.QueryAsync(new() { Search = "Example", StartIndex = 1 }, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Equal(0, later.TotalRecordCount);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancelledRefreshWaiterDoesNotFetchAndDoesNotBlockPaging()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fetch = async (_, token) =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return Fixture("list.html");
        };
        var first = Query("Example");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var waiting = _service.QueryAsync(new() { Search = "Other" }, new Dictionary<string, Guid>(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // Local pages must complete while the first remote request still owns the lock.
        var local = await _service.QueryAsync(new(), new Dictionary<string, Guid>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var page = await _service.QueryAsync(new() { Search = "Example", StartIndex = 50 }, new Dictionary<string, Guid>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(local.Items);
        Assert.Empty(page.Items);
        finish.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(SearchRequests);

        _clock.Now = _clock.Now.AddHours(24);
        await Query("Example").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, SearchRequests.Count());
    }

    [Fact]
    public async Task CancelledRefreshReleasesLockForNextCaller()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fetch = (_, token) => { entered.SetResult(); return pending.Task.WaitAsync(token); };
        using var cancellation = new CancellationTokenSource();
        var first = _service.QueryAsync(new() { Search = "Example" }, new Dictionary<string, Guid>(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        _fetch = (_, _) => Task.FromResult(Fixture("list.html"));
        var next = await Query("Example").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, next.TotalRecordCount);
        Assert.Equal(2, SearchRequests.Count());
    }

    [Fact]
    public async Task QueryCombinesMetadataAndInclusiveDatesBeforePaging()
    {
        await Seed(
            new() { Number = "ABP-100", Actresses = ["Actor"], Genres = ["Genre"], Studio = "Studio", ReleaseDate = _clock.Now },
            new() { Number = "ABP-101", Actresses = ["Actor"], Genres = ["Other"], Studio = "Studio", ReleaseDate = _clock.Now },
            new() { Number = "ABP-102", Actresses = ["Actor"], Genres = ["Genre"], Studio = "Studio", ReleaseDate = _clock.Now.AddDays(-1) },
            new() { Number = "ABP-103", Actresses = ["Other"], Genres = ["Genre"], Studio = "Studio", ReleaseDate = _clock.Now },
            new() { Number = "ABP-104", Actresses = ["Actor"], Genres = ["Genre"], Studio = "Other", ReleaseDate = _clock.Now },
            new() { Number = "ABP-105", Actresses = ["Actor"], Genres = ["Genre"], Studio = "Studio", ReleaseDate = _clock.Now.AddDays(1) },
            new() { Number = "ABP-106", Actresses = ["Actor"], Genres = ["Genre"], Studio = "Studio" });
        var page = await _service.QueryAsync(new() { Actress = "actor", Genre = "genre", Studio = "studio", ReleasedFrom = _clock.Now, ReleasedTo = _clock.Now, Limit = 1 }, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Equal("ABP-100", Assert.Single(page.Items).Number);
        Assert.Equal(1, page.TotalRecordCount);
    }

    [Theory]
    [InlineData("abp-100", "ABP-100")]
    [InlineData("unique title", "ABP-100")]
    [InlineData("actor", "ABP-101")]
    [InlineData("genre", "ABP-102")]
    [InlineData("studio", "ABP-103")]
    public async Task SearchMatchesEverySupportedFieldIgnoringCase(string term, string expected)
    {
        _config.SelectedLibraryId = Guid.Empty;
        await Seed(new() { Number = "ABP-100", Title = "Unique Title" }, new() { Number = "ABP-101", Actresses = ["Actor"] },
            new() { Number = "ABP-102", Genres = ["Genre"] }, new() { Number = "ABP-103", Studio = "Studio" });
        Assert.Equal(expected, Assert.Single((await Query(term)).Items).Number);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData(CatalogSortField.ViewCount, true, "ABP-100", "SSIS-123")]
    [InlineData(CatalogSortField.ViewCount, false, "LOW-001", "ABP-100")]
    [InlineData(CatalogSortField.FavoriteCount, true, "ABP-100", "SSIS-123")]
    [InlineData(CatalogSortField.ReleaseDate, false, "LOW-001", "ABP-100")]
    public async Task QuerySortsNullLastWithNumberTies(CatalogSortField sort, bool descending, string first, string second)
    {
        await Seed(
            new() { Number = "SSIS-123", ViewCount = 20, FavoriteCount = 20, ReleaseDate = _clock.Now },
            new() { Number = "ABP-100", ViewCount = 20, FavoriteCount = 20, ReleaseDate = _clock.Now },
            new() { Number = "NULL-001" },
            new() { Number = "LOW-001", ViewCount = 1, FavoriteCount = 1, ReleaseDate = _clock.Now.AddDays(-1) });
        var page = await _service.QueryAsync(new() { Sort = sort, Descending = descending }, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Equal(first, page.Items[0].Number);
        Assert.Equal(second, page.Items[1].Number);
        Assert.Equal("NULL-001", page.Items[^1].Number);
    }

    [Theory]
    [InlineData(-3, -10, 0, 1)]
    [InlineData(-1, 200, 0, 100)]
    [InlineData(2, 0, 2, 1)]
    public async Task PagingClampsAtModelBoundaryAndPreservesTotal(int start, int limit, int expectedStart, int expectedLimit)
    {
        var query = new CatalogQuery { StartIndex = start, Limit = limit };
        Assert.Equal(expectedStart, query.StartIndex);
        Assert.Equal(expectedLimit, query.Limit);
        await Seed(Enumerable.Range(1, 105).Select(i => new JableWork { Number = $"ABP-{i:000}" }).ToArray());
        var page = await _service.QueryAsync(query, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Equal(105, page.TotalRecordCount);
        Assert.Equal(expectedLimit, page.Items.Length);
        Assert.Equal($"ABP-{expectedStart + 1:000}", page.Items[0].Number);
    }

    [Fact]
    public async Task CancellationDoesNotCommitPartialRecentData()
    {
        await Seed(new JableWork { Number = "OLD-001" });
        using var cancellation = new CancellationTokenSource();
        _fetch = (uri, token) =>
        {
            if (uri.AbsolutePath.EndsWith("/2/", StringComparison.Ordinal)) cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Fixture("list.html"));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.SyncRecentAsync(new InlineProgress(), cancellation.Token));
        Assert.Equal("OLD-001", Assert.Single(_store.Snapshot.Works).Key);
    }

    [Fact]
    public void RegistratorUsesConcreteSingletonsOnly()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        Assert.Equal(new[] { typeof(JableParser), typeof(JableHttpClient), typeof(CatalogStore), typeof(JableCatalogService), typeof(LibraryAccessService) }, services.Select(x => x.ServiceType));
        Assert.All(services, registration => Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime));
        services.AddSingleton(_store);
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<JableCatalogService>(), provider.GetRequiredService<JableCatalogService>());
    }

    [Fact]
    public async Task ClearSearchPersistsOnlyRecentWorksAndPreservesEnrichedDetails()
    {
        await _service.SearchRemoteAsync("Example", CancellationToken.None);
        await _store.MutateAsync(snapshot =>
        {
            var recent = snapshot.Works["SSIS-123"];
            recent.InRecent = true;
            recent.Work.Actresses = ["Actor"];
            recent.Work.Genres = ["Genre"];
            recent.Work.Studio = "Studio";
            recent.Work.Duration = TimeSpan.FromMinutes(90);
            recent.Work.ReleaseDate = _clock.Now.AddYears(-1);
            snapshot.LastSuccessfulSync = _clock.Now;
            return snapshot;
        }, CancellationToken.None);

        await _service.ClearSearchAsync(CancellationToken.None);

        var fresh = new CatalogStore(_directory);
        await fresh.EnsureLoadedAsync(CancellationToken.None);
        var entry = Assert.Single(fresh.Snapshot.Works).Value;
        Assert.Equal("SSIS-123", entry.Work.Number);
        Assert.True(entry.InRecent);
        Assert.Null(entry.SearchExpiresAt);
        Assert.Equal(["Actor"], entry.Work.Actresses);
        Assert.Equal(["Genre"], entry.Work.Genres);
        Assert.Equal("Studio", entry.Work.Studio);
        Assert.Equal(TimeSpan.FromMinutes(90), entry.Work.Duration);
        Assert.Equal(_clock.Now.AddYears(-1), entry.Work.ReleaseDate);
        Assert.Equal(_clock.Now, fresh.Snapshot.LastSuccessfulSync);
        Assert.Empty(fresh.Snapshot.SearchCache);
        Assert.Single(SearchRequests);
    }

    [Fact]
    public async Task ClearSearchRemovesFailedSearchFallbackMarkers()
    {
        await _service.SearchRemoteAsync("Example", CancellationToken.None);
        _clock.Now = _clock.Now.AddHours(24);
        _fetch = (_, _) => throw new HttpRequestException("offline");
        Assert.NotEmpty((await Query("Example")).Items);
        await _service.ClearSearchAsync(CancellationToken.None);
        // An expired entry introduced later must not inherit the cleared term's failure marker.
        await _store.ReplaceAsync(new CatalogSnapshot
        {
            Works = new() { ["ABP-001"] = new() { Work = Complete(new() { Number = "ABP-001", Title = "Example" }), SearchExpiresAt = _clock.Now.AddHours(-1) } },
        }, CancellationToken.None);
        var page = await _service.QueryAsync(new() { Search = "Example", StartIndex = 1 }, new Dictionary<string, Guid>(), CancellationToken.None);
        Assert.Equal(0, page.TotalRecordCount);
    }

    [Fact]
    public async Task ClearSearchWaitsForActiveRefreshSoOldResultsCannotReappear()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fetch = (_, token) => { entered.TrySetResult(); return finish.Task.WaitAsync(token); };
        var query = Query("Example");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clearing = _service.ClearSearchAsync(CancellationToken.None);
        finish.SetResult(Fixture("list.html"));
        await Task.WhenAll(query, clearing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(_store.Snapshot.Works);
        Assert.Empty(_store.Snapshot.SearchCache);
    }

    private Task<CatalogPageDto> Query(string search) => _service.QueryAsync(new() { Search = search }, new Dictionary<string, Guid>(), CancellationToken.None);
    private IEnumerable<Uri> SearchRequests => _requests.Where(uri => uri.AbsolutePath.StartsWith("/search/"));
    private static JableWork Complete(JableWork work)
    {
        if (work.Title.Length == 0) work.Title = work.Number;
        if (work.CanonicalUrl.Length == 0) work.CanonicalUrl = $"https://jable.tv/videos/{work.Number.ToLowerInvariant()}/";
        return work;
    }
    private Task Seed(params JableWork[] works) => _store.ReplaceAsync(new CatalogSnapshot
    {
        Works = works.ToDictionary(work => work.Number, work => new CatalogEntry { Work = Complete(work), InRecent = true }, StringComparer.OrdinalIgnoreCase)
    }, CancellationToken.None);
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static string Card(string number, string title, string counts) => $"<div class='col-6 col-sm-4 col-lg-3'><a href='/videos/{number}/'><h6>{number} {title}</h6></a><p class='sub-title'>{counts}</p></div>";
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class InlineProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
    }
}
