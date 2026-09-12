using System.Net;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable.Models;
using Jellyfin.Plugin.Jable.Providers;
using Jellyfin.Plugin.Jable.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class FinalRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jable-final-" + Guid.NewGuid());
    private readonly PluginConfiguration _config = new() { SelectedLibraryId = Guid.NewGuid(), RecentPageCount = 3, MinimumRequestIntervalMs = 250 };
    private readonly CatalogStore _store;
    public FinalRegressionTests() => _store = new(_directory);
    private JableCatalogService Catalog(Func<Uri, CancellationToken, Task<string>> fetch) => new(fetch, new(), _store, () => _config, TimeProvider.System);
    private static string Fixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
    private static CatalogSnapshot OldSnapshot() => new() { Works = new() { ["OLD-001"] = new() { Work = new() { Number = "OLD-001", Title = "Old", CanonicalUrl = "https://jable.tv/videos/old-001/" }, InRecent = true } }, LastSuccessfulSync = DateTimeOffset.UnixEpoch };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task N1_DetailChallengeStopsAfterCurrentBatchWithoutPublishing(bool search)
    {
        _config.RecentPageCount = 1;
        await _store.ReplaceAsync(OldSnapshot(), default);
        var html = string.Concat(Enumerable.Range(1, 3).Select(index => Fixture("list.html")
            .Replace("ssis-123", $"ssis-{index}23").Replace("SSIS-123", $"SSIS-{index}23")
            .Replace("abp-456", $"abp-{index}56").Replace("ABP-456", $"ABP-{index}56")));
        Assert.Equal(6, new JableParser().ParseList(html, new("https://jable.tv/latest-updates/"), DateTimeOffset.UtcNow).Works.Count);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var settled = 0;
        var catalog = Catalog(async (uri, token) =>
        {
            if (!uri.AbsolutePath.StartsWith("/videos/")) return html;
            if (Interlocked.Increment(ref requests) == 2) bothEntered.TrySetResult();
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            Interlocked.Increment(ref settled);
            return Fixture("challenge.html");
        });
        if (search)
        {
            var error = await Assert.ThrowsAsync<JableRequestException>(() => catalog.SearchRemoteAsync("actor", default));
            Assert.Equal(JableFailureKind.Challenge, error.Kind);
        }
        else await catalog.SyncRecentAsync(new Progress<double>(), default);
        Assert.InRange(requests, 1, 2);
        Assert.Equal(requests, settled);
        Assert.Equal("OLD-001", Assert.Single(_store.Snapshot.Works).Key);
        Assert.True(_store.Snapshot.Works["OLD-001"].InRecent);
        Assert.Equal(DateTimeOffset.UnixEpoch, _store.Snapshot.LastSuccessfulSync);
        Assert.Empty(_store.Snapshot.SearchCache);
        Assert.Empty(_store.Snapshot.SearchHits);
        Assert.Contains("challenge", _store.Snapshot.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task N1_ChallengeErrorSurvivesLaterNetworkFailureInSameBatch(bool search)
    {
        _config.RecentPageCount = 1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var catalog = Catalog(async (uri, _) =>
        {
            if (!uri.AbsolutePath.StartsWith("/videos/")) return Fixture("list.html");
            if (uri.AbsolutePath == "/videos/ssis-123/") return Fixture("challenge.html");
            while (!_store.Snapshot.LastError.Contains("challenge", StringComparison.OrdinalIgnoreCase))
                await Task.Delay(1, timeout.Token);
            throw new JableRequestException(JableFailureKind.Network, "later network failure");
        });
        if (search) await Assert.ThrowsAsync<JableRequestException>(() => catalog.SearchRemoteAsync("actor", default));
        else await catalog.SyncRecentAsync(new Progress<double>(), default);
        Assert.Contains("challenge", _store.Snapshot.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_store.Snapshot.Works);
        Assert.Empty(_store.Snapshot.SearchCache);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task N1_OrdinaryDetailNetworkFailureStillPublishesValidatedListWork(bool search)
    {
        _config.RecentPageCount = 1;
        var catalog = Catalog((uri, _) =>
        {
            if (uri.AbsolutePath == "/videos/abp-456/") throw new JableRequestException(JableFailureKind.Network, "detail unavailable");
            return Task.FromResult(Fixture(uri.AbsolutePath.StartsWith("/videos/") ? "detail.html" : "list.html"));
        });
        if (search) Assert.Equal(2, (await catalog.SearchRemoteAsync("actor", default)).Count);
        else await catalog.SyncRecentAsync(new Progress<double>(), default);
        Assert.Equal(2, _store.Snapshot.Works.Count);
        Assert.Equal("ABP-456 Another title", _store.Snapshot.Works["ABP-456"].Work.Title);
        Assert.Equal(980123, _store.Snapshot.Works["ABP-456"].Work.ViewCount);
        Assert.Empty(_store.Snapshot.LastError);
        if (search)
        {
            Assert.True(_store.Snapshot.SearchCache.ContainsKey("actor"));
            Assert.Equal(2, _store.Snapshot.SearchHits["actor"].Length);
        }
        else
        {
            Assert.NotNull(_store.Snapshot.LastSuccessfulSync);
            Assert.All(_store.Snapshot.Works.Values, entry => Assert.True(entry.InRecent));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task N1_EnrichmentCancellationPropagatesAndReleasesBothDetailSlots(bool search)
    {
        _config.RecentPageCount = 1;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var canceled = false;
        var catalog = Catalog(async (uri, token) =>
        {
            if (!uri.AbsolutePath.StartsWith("/videos/")) return Fixture("list.html");
            if (!canceled)
            {
                if (Interlocked.Increment(ref requests) == 2) bothEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Fixture("detail.html");
        });
        using var cancel = new CancellationTokenSource();
        Task pending = search ? catalog.SearchRemoteAsync("actor", cancel.Token) : catalog.SyncRecentAsync(new Progress<double>(), cancel.Token);
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(_store.Snapshot.Works);
        Assert.Empty(_store.Snapshot.SearchCache);
        canceled = true;
        var results = await Task.WhenAll(catalog.GetExactAsync("SSIS-123", default), catalog.GetExactAsync("SSIS-123", default)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.All(results, work => Assert.NotNull(work));
    }

    [Theory]
    [InlineData("http://user@localhost:8080", "", false, "old-secret")]
    [InlineData("http://user:@localhost:8080", "", false, "old-secret")]
    [InlineData("http://user:url-secret@localhost:8080", "", false, "url-secret")]
    [InlineData("http://user@localhost:8080", "new-secret", false, "new-secret")]
    [InlineData("http://user:url-secret@localhost:8080", "new-secret", false, "new-secret")]
    [InlineData("http://user:@localhost:8080", "", true, "")]
    [InlineData("http://user:url-secret@localhost:8080", "new-secret", true, "")]
    public void N2_RealConfigurationUpdatePreservesOrExplicitlyReplacesXmlPassword(string url, string input, bool clear, string expected)
    {
        var paths = DispatchProxy.Create<IApplicationPaths, Proxy>();
        ((Proxy)(object)paths).Call = (_, _) => _directory;
        var serializer = DispatchProxy.Create<IXmlSerializer, Proxy>();
        string xml = "";
        ((Proxy)(object)serializer).Call = (method, args) =>
        {
            if (method.Name == "DeserializeFromFile") return new PluginConfiguration { ProxyPassword = "old-secret" };
            using var writer = new StringWriter();
            new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, args![0]);
            xml = writer.ToString();
            return null;
        };
        var plugin = new Plugin(paths, serializer);
        var incoming = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(new
        {
            ProxyUrl = url, ProxyUsername = "", NewProxyPassword = input, ClearProxyPassword = clear
        }), JsonDefaults.Options)!;
        plugin.UpdateConfiguration(incoming);
        Assert.Equal(expected, plugin.Configuration.ProxyPassword);
        Assert.Equal("http://localhost:8080/", plugin.Configuration.ProxyUrl);
        Assert.Equal("user", plugin.Configuration.ProxyUsername);
        using var reader = new StringReader(xml);
        var persisted = (PluginConfiguration)new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
        Assert.Equal(expected, persisted.ProxyPassword);
        Assert.Equal("user", persisted.ProxyUsername);
        Assert.Equal("http://localhost:8080/", persisted.ProxyUrl);
        Assert.DoesNotContain("NewProxyPassword", xml);
        Assert.DoesNotContain("ClearProxyPassword", xml);
        var json = JsonSerializer.Serialize(plugin.Configuration, JsonDefaults.Options);
        foreach (var secret in new[] { "old-secret", "url-secret", "new-secret" }) Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("ProxyPassword", json);
    }

    [Fact]
    public void N2_ProxyUrlSetterDoesNotMutatePersistedPassword()
    {
        var config = new PluginConfiguration { ProxyPassword = "old-secret" };
        config.ProxyUrl = "http://user:url-secret@localhost:8080";
        Assert.Equal("old-secret", config.ProxyPassword);
        Assert.Equal("http://localhost:8080/", config.ProxyUrl);
    }

    [Theory]
    [InlineData("ssis-123.456")]
    [InlineData("ssis-123-extra")]
    [InlineData("ssis-123-ssis-124")]
    [InlineData("ssis-123/extra")]
    [InlineData("ssis-123%2Fextra")]
    [InlineData("ssis-123.mp4")]
    [InlineData("ssis-123/?tracking=1")]
    public async Task I1_CanonicalMustBeOneCompleteIdentifierThroughProvider(string slug)
    {
        var html = Fixture("detail.html").Replace("/videos/ssis-123/", $"/videos/{slug}/");
        var catalog = Catalog((_, _) => Task.FromResult(html));
        using var client = new JableHttpClient(new ReplyHandler((_, _) => throw new Exception("No image fetch expected")), () => _config);
        var access = new LibraryAccessService(() => _config, _ => null,
            _ => [new Folder { Id = _config.SelectedLibraryId }],
            () => [new VirtualFolderInfo { ItemId = _config.SelectedLibraryId.ToString(), Locations = ["/selected"] }], _ => []);
        var provider = new JableMovieProvider(catalog, client, access);
        Assert.False((await provider.GetMetadata(new MovieInfo { Name = "SSIS-123", Path = "/selected/SSIS-123.mp4" }, default)).HasMetadata);
        Assert.Empty(_store.Snapshot.Works);
        Assert.Empty(new JableParser().ParseList(Fixture("list.html").Replace("/videos/ssis-123/", $"/videos/{slug}/").Replace("/videos/abp-456/", $"/videos/{slug}/"), new("https://jable.tv/latest-updates/"), DateTimeOffset.UtcNow).Works);
    }

    [Fact]
    public void I2_IntegerCountsStaySeparate()
    {
        var work = new JableParser().ParseList(Fixture("list.html"), new("https://jable.tv/latest-updates/"), DateTimeOffset.UtcNow).Works[1];
        Assert.Equal(980123, work.ViewCount);
        Assert.Equal(9001, work.FavoriteCount);
        var plain = new JableParser().ParseList(Fixture("list.html").Replace("980,123 9,001", "980123 9001"), new("https://jable.tv/latest-updates/"), DateTimeOffset.UtcNow).Works[1];
        Assert.Equal(980123, plain.ViewCount);
        Assert.Equal(9001, plain.FavoriteCount);
    }

    [Fact]
    public void I2_CountOverflowIsMissing() => Assert.Null(JableParser.ParseCount("79228162514264337593543950335M"));

    [Theory]
    [InlineData("演员乙")]
    [InlineData("独占标签")]
    public async Task I3_RemoteHitsPersistForOfflinePagingWithoutTextMatch(string term)
    {
        var catalog = Catalog((uri, _) => uri.AbsolutePath.StartsWith("/videos/") ? throw new HttpRequestException("detail offline") : Task.FromResult(Fixture("list.html")));
        var first = await catalog.QueryAsync(new() { Search = term, Limit = 1 }, new Dictionary<string, Guid>(), default);
        Assert.Equal(2, first.TotalRecordCount);
        var reloaded = new CatalogStore(_directory);
        var offline = new JableCatalogService((_, _) => throw new HttpRequestException("offline"), new(), reloaded, () => _config, TimeProvider.System);
        var second = await offline.QueryAsync(new() { Search = term.ToUpperInvariant(), StartIndex = 1, Limit = 1 }, new Dictionary<string, Guid>(), default);
        Assert.Equal(2, second.TotalRecordCount);
        Assert.NotEqual(Assert.Single(first.Items).Number, Assert.Single(second.Items).Number);
    }

    [Fact]
    public async Task I3_RecentSyncEnrichesDatesAndCategoriesFromEmptyCache()
    {
        _config.RecentPageCount = 1;
        var catalog = Catalog((uri, _) => Task.FromResult(uri.AbsolutePath.StartsWith("/videos/")
            ? Fixture("detail.html").Replace("SSIS-123", uri.AbsolutePath.Contains("abp") ? "ABP-456" : "SSIS-123").Replace("ssis-123", uri.AbsolutePath.Contains("abp") ? "abp-456" : "ssis-123")
            : Fixture("list.html")));
        await catalog.SyncRecentAsync(new Progress<double>(), default);
        var page = await catalog.QueryAsync(new() { Actress = "演员甲", Genre = "中文字幕", ReleasedFrom = DateTimeOffset.UtcNow.AddDays(-4), Studio = "Example Studio" }, new Dictionary<string, Guid>(), default);
        Assert.Equal(2, page.TotalRecordCount);
        Assert.All(page.Items, work => Assert.Equal(TimeSpan.FromMinutes(120), work.Duration));
    }

    [Fact]
    public void I4_PasswordNeverSerializesWithJellyfinJsonOptions()
    {
        var config = new PluginConfiguration { ProxyPassword = "synthetic-secret", ProxyUrl = "http://user:synthetic-url-secret@localhost:1080" };
        var json = JsonSerializer.Serialize(config, JsonDefaults.Options);
        Assert.DoesNotContain("synthetic-secret", json);
        Assert.DoesNotContain("synthetic-url-secret", json);
        Assert.DoesNotContain("@localhost", json);
    }

    [Fact]
    public void I4_ConfigurationWritesMergeSecretAndPersistOnlyXml()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, Proxy>();
        ((Proxy)(object)paths).Call = (_, _) => _directory;
        var serializer = DispatchProxy.Create<IXmlSerializer, Proxy>();
        string xml = "";
        ((Proxy)(object)serializer).Call = (method, args) =>
        {
            if (method.Name == "DeserializeFromFile") return new PluginConfiguration { ProxyPassword = "old-secret" };
            using var writer = new StringWriter();
            new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, args![0]);
            xml = writer.ToString();
            return null;
        };
        var plugin = new Plugin(paths, serializer);
        void Save(string json) => plugin.UpdateConfiguration(JsonSerializer.Deserialize<PluginConfiguration>(json, JsonDefaults.Options)!);
        Save("{\"ProxyUrl\":\"http://localhost:8080\",\"NewProxyPassword\":\"\"}");
        Assert.Equal("old-secret", plugin.Configuration.ProxyPassword);
        Save("{\"NewProxyPassword\":\"new-secret\"}");
        Assert.Contains("<ProxyPassword>new-secret</ProxyPassword>", xml);
        Assert.DoesNotContain("NewProxyPassword", xml);
        Assert.DoesNotContain("new-secret", JsonSerializer.Serialize(plugin.Configuration, JsonDefaults.Options));
        Save("{\"ProxyUrl\":\"http://url-user:url-secret@localhost:8080\",\"ProxyUsername\":\"\"}");
        Assert.Equal("url-user", plugin.Configuration.ProxyUsername);
        Assert.Equal("url-secret", plugin.Configuration.ProxyPassword);
        Assert.Equal("http://localhost:8080/", plugin.Configuration.ProxyUrl);
        Save("{\"ClearProxyPassword\":true}");
        Assert.Empty(plugin.Configuration.ProxyPassword);
    }

    [Theory]
    [InlineData("ftp://localhost", 20, 15, 750)]
    [InlineData("http://localhost/path", 20, 15, 750)]
    [InlineData("", 101, 15, 750)]
    [InlineData("", 0, 15, 750)]
    [InlineData("", 20, 4, 750)]
    [InlineData("", 20, 61, 750)]
    [InlineData("", 20, 15, 249)]
    public void I5_ConfigurationUpdateRejectsInvalidWithoutReplacing(string proxy, int pages, int timeout, int interval)
    {
        var paths = DispatchProxy.Create<IApplicationPaths, Proxy>();
        ((Proxy)(object)paths).Call = (_, _) => _directory;
        var serializer = DispatchProxy.Create<IXmlSerializer, Proxy>();
        ((Proxy)(object)serializer).Call = (method, _) => method.Name == "DeserializeFromFile" ? new PluginConfiguration() : null;
        var plugin = new Plugin(paths, serializer);
        var before = plugin.Configuration;
        Assert.Throws<ArgumentException>(() => plugin.UpdateConfiguration(new PluginConfiguration { ProxyUrl = proxy, RecentPageCount = pages, RequestTimeoutSeconds = timeout, MinimumRequestIntervalMs = interval }));
        Assert.Same(before, plugin.Configuration);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":99,\"works\":{},\"searchCache\":{}}")]
    [InlineData("{\"works\":{\"SSIS-123\":null},\"searchCache\":{}}")]
    [InlineData("{\"works\":null,\"searchCache\":{}}")]
    [InlineData("{\"works\":{},\"searchCache\":null}")]
    [InlineData("{\"works\":{},\"searchHits\":null}")]
    [InlineData("{\"works\":{},\"searchHits\":{\"term\":[\"SSIS123\"]}}")]
    [InlineData("{\"works\":{\"SSIS-123\":{\"work\":{\"number\":\"SSIS-123\"}}}}")]
    [InlineData("{\"works\":{\"SSIS-123\":{\"work\":null}},\"searchCache\":{}}")]
    [InlineData("{\"works\":{\"SSIS-123\":{\"work\":{\"number\":\"ABP-456\",\"title\":\"Wrong\",\"canonicalUrl\":\"https://jable.tv/videos/abp-456/\"}}},\"searchCache\":{}}")]
    public async Task I6_StructuralCorruptionFallsBackToPrevious(string invalid)
    {
        await _store.ReplaceAsync(OldSnapshot(), default);
        await _store.ReplaceAsync(OldSnapshot(), default);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Jable", "catalog.json"), invalid);
        var reloaded = new CatalogStore(_directory);
        await reloaded.EnsureLoadedAsync(default);
        Assert.True(reloaded.Snapshot.IsRecovered);
        Assert.Equal("OLD-001", Assert.Single(reloaded.Snapshot.Works).Key);
    }

    [Fact]
    public async Task I6_BothCorruptRejectWritesAndRemoteFailureCannotOverwrite()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Jable"));
        var path = Path.Combine(_directory, "Jable", "catalog.json");
        var previous = Path.Combine(_directory, "Jable", "catalog.previous.json");
        await File.WriteAllTextAsync(path, "bad current");
        await File.WriteAllTextAsync(previous, "bad previous");
        await _store.EnsureLoadedAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ReplaceAsync(OldSnapshot(), default));
        var catalog = Catalog((_, _) => throw new HttpRequestException("offline"));
        await catalog.SyncRecentAsync(new Progress<double>(), default);
        await catalog.QueryAsync(new() { Search = "actor" }, new Dictionary<string, Guid>(), default);
        Assert.Equal("bad current", await File.ReadAllTextAsync(path));
        Assert.Equal("bad previous", await File.ReadAllTextAsync(previous));
        Assert.Contains("read-only", (await catalog.GetStatusAsync(default)).LastError);
        Assert.True((await catalog.GetStatusAsync(default)).IsReadOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.MutateAsync(snapshot => snapshot, default));
    }

    [Fact]
    public async Task I7_ThreeBlockedDetailCallsUseAtMostTwoSlotsAndCancellationReleases()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        using var client = new JableHttpClient(new ReplyHandler(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            if (current >= 2) twoEntered.TrySetResult();
            try { await release.Task.WaitAsync(token); return new(HttpStatusCode.OK) { Content = new StringContent(Fixture("detail.html")) }; }
            finally { Interlocked.Decrement(ref active); }
        }), () => _config);
        var catalog = Catalog(client.GetHtmlAsync);
        using var cancel = new CancellationTokenSource();
        var calls = new[] { catalog.GetExactAsync("SSIS-123", default), catalog.GetExactAsync("SSIS-123", default), catalog.GetExactAsync("SSIS-123", cancel.Token) };
        await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(600);
        cancel.Cancel();
        release.SetResult();
        await Task.WhenAll(calls.Take(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calls[2]);
        Assert.Equal(2, maximum);
        Assert.NotNull(await catalog.GetExactAsync("SSIS-123", default));
    }

    [Theory]
    [InlineData("https://jable.tv/latest-updates/")]
    [InlineData("https://attacker.invalid/")]
    [InlineData("cycle")]
    public async Task I8_InvalidNextKeepsWholePreviousBatch(string next)
    {
        await _store.ReplaceAsync(OldSnapshot(), default);
        var catalog = Catalog((uri, _) => Task.FromResult(Fixture("list.html").Replace("https://jable.tv/latest-updates/2/", next == "cycle" ? (uri.AbsolutePath.EndsWith("/2/") ? "https://jable.tv/latest-updates/" : "https://jable.tv/latest-updates/2/") : next)));
        await catalog.SyncRecentAsync(new Progress<double>(), default);
        Assert.Equal("OLD-001", Assert.Single(_store.Snapshot.Works).Key);
        Assert.Equal(DateTimeOffset.UnixEpoch, _store.Snapshot.LastSuccessfulSync);
        Assert.Contains("next", _store.Snapshot.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearSearchDiscardsEarlierDirectProducer(bool detail)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = Catalog(async (_, token) => { entered.TrySetResult(); await finish.Task.WaitAsync(token); return Fixture(detail ? "detail.html" : "list.html"); });
        Task pending = detail ? catalog.GetExactAsync("SSIS-123", default) : catalog.SearchRemoteAsync("actor", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await catalog.ClearSearchAsync(default);
        finish.SetResult();
        await pending;
        Assert.Empty(_store.Snapshot.Works);
        Assert.Empty(_store.Snapshot.SearchCache);
        Assert.Empty(_store.Snapshot.SearchHits);
    }

    [Fact]
    public async Task I6_InvalidReplacementPreservesValidFilesAndOptionalArraysNormalize()
    {
        var snapshot = OldSnapshot();
        snapshot.Works["OLD-001"].Work.Actresses = [null!, " Actor ", "Actor"];
        snapshot.Works["OLD-001"].Work.Genres = null!;
        await _store.ReplaceAsync(snapshot, default);
        Assert.Equal(["Actor"], _store.Snapshot.Works["OLD-001"].Work.Actresses);
        Assert.Empty(_store.Snapshot.Works["OLD-001"].Work.Genres);
        var path = Path.Combine(_directory, "Jable", "catalog.json");
        var bytes = await File.ReadAllBytesAsync(path);
        snapshot.Works["OLD-001"].Work.CanonicalUrl = "https://jable.tv/videos/old-001-extra/";
        await Assert.ThrowsAsync<JsonException>(() => _store.ReplaceAsync(snapshot, default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(Path.Combine(_directory, "Jable", "catalog.tmp")));
    }

    [Fact]
    public async Task I3_SearchHitsExpireAndClearWithTheirTerm()
    {
        var catalog = Catalog((_, _) => Task.FromResult(Fixture("list.html")));
        await catalog.SearchRemoteAsync("Actor", default);
        Assert.Equal(["SSIS123", "ABP456"], _store.Snapshot.SearchHits["ACTOR"]);
        await _store.ClearExpiredSearchEntriesAsync(DateTimeOffset.UtcNow.AddDays(2), default);
        Assert.Empty(_store.Snapshot.SearchHits);
        Assert.Empty(_store.Snapshot.Works);
    }

    [Fact]
    public async Task I6_DuplicateCaseInsensitiveTermsRecover()
    {
        await _store.ReplaceAsync(OldSnapshot(), default);
        await _store.ReplaceAsync(OldSnapshot(), default);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Jable", "catalog.json"), "{\"works\":{},\"searchCache\":{\"Term\":\"2026-09-12T00:00:00Z\",\"term\":\"2026-09-12T00:00:00Z\"}}");
        var reloaded = new CatalogStore(_directory);
        await reloaded.EnsureLoadedAsync(default);
        Assert.True(reloaded.Snapshot.IsRecovered);
        Assert.Single(reloaded.Snapshot.Works);
    }

    [Fact]
    public async Task I6_EquivalentIdentityKeysRemainLookupable()
    {
        var snapshot = OldSnapshot();
        var entry = snapshot.Works["OLD-001"];
        snapshot.Works.Clear();
        snapshot.Works["old001"] = entry;
        await _store.ReplaceAsync(snapshot, default);
        Assert.NotNull(await Catalog((_, _) => throw new Exception("No remote fetch")).GetCachedAsync("OLD-001", default));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
    private sealed class ReplyHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }
}
