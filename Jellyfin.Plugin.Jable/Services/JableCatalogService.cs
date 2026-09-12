using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable.Models;
using System.Net.Http;
using System.Collections.Concurrent;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.Jable.Services;

public sealed class JableCatalogService
{
    private readonly Func<Uri, CancellationToken, Task<string>> _fetch;
    private readonly JableParser _parser;
    private readonly CatalogStore _store;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, byte> _failedSearchTerms = new(StringComparer.OrdinalIgnoreCase);
    // ponytail: global search refresh lock; use per-term locks if concurrent search throughput matters
    private readonly SemaphoreSlim _searchRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _detailGate = new(2, 2);
    private long _searchGeneration;

    public JableCatalogService(JableHttpClient client, JableParser parser, CatalogStore store, TimeProvider? timeProvider = null)
        : this(client.GetHtmlAsync, parser, store, () => Plugin.Instance?.Configuration ?? new PluginConfiguration(), timeProvider ?? TimeProvider.System)
    {
    }

    public JableCatalogService(Func<Uri, CancellationToken, Task<string>> fetch, JableParser parser, CatalogStore store, Func<PluginConfiguration> configuration, TimeProvider timeProvider)
    {
        _fetch = fetch;
        _parser = parser;
        _store = store;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    public async Task SyncRecentAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_configuration().SelectedLibraryId == Guid.Empty || _store.Snapshot.IsReadOnly) return;

        var count = Math.Clamp(_configuration().RecentPageCount, 1, 100);
        var recent = new Dictionary<string, JableWork>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<Uri>();
        Uri? uri = new("https://jable.tv/latest-updates/");
        progress.Report(0);
        try
        {
            for (var pageIndex = 0; pageIndex < count && uri is not null; pageIndex++)
            {
                visited.Add(uri);
                var html = await FetchAsync(uri, cancellationToken).ConfigureAwait(false);
                var page = _parser.ParseList(html, uri, _timeProvider.GetUtcNow());
                if (page.HasNextLink && (page.NextPageUri is null || visited.Contains(page.NextPageUri)))
                {
                    await RecordErrorAsync("Jable recent next link is invalid or cyclic (parse failure).", cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (page.Works.Count == 0)
                {
                    await RecordErrorAsync("Jable recent page could not be parsed: no validated works.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                foreach (var work in page.Works)
                {
                    if (recent.TryGetValue(work.Number, out var previous)) Merge(previous, work, detail: false);
                    else recent[work.Number] = work;
                }

                uri = page.NextPageUri;
                progress.Report(99d * (pageIndex + 1) / count);
            }

            await EnrichAsync(recent.Values.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRemoteFailure(exception))
        {
            return; // FetchAsync recorded the error; no recent data has been changed.
        }

        var now = _timeProvider.GetUtcNow();
        await _store.MutateAsync(snapshot =>
        {
            foreach (var entry in snapshot.Works.Values) entry.InRecent = false;
            foreach (var work in recent.Values) MergeEntry(snapshot, work, detail: true).InRecent = true;
            RemoveExpired(snapshot, now);
            snapshot.LastSuccessfulSync = now;
            snapshot.LastError = string.Empty;
            return snapshot;
        }, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public async Task<JableWork?> GetExactAsync(string number, CancellationToken cancellationToken)
    {
        await _store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_configuration().SelectedLibraryId == Guid.Empty || NumberParser.ParseExact(number) is not { } normalized) return null;
        if (_store.Snapshot.IsReadOnly) return await GetCachedAsync(number, cancellationToken).ConfigureAwait(false);
        var generation = Volatile.Read(ref _searchGeneration);

        var work = await FetchDetailAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (work is null)
        {
            await RecordErrorAsync("Jable detail identity did not match the requested number.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        await CacheSearchAsync(number.Trim(), [work], generation, cancellationToken).ConfigureAwait(false);
        return _store.Snapshot.Works.TryGetValue(work.Number, out var entry) ? entry.Work : work;
    }

    public async Task<JableWork?> GetCachedAsync(string number, CancellationToken cancellationToken)
    {
        await _store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return NumberParser.ParseExact(number) is { } normalized && _store.Snapshot.Works.TryGetValue(normalized, out var entry) ? entry.Work : null;
    }

    public async Task<IReadOnlyList<JableWork>> SearchRemoteAsync(string query, CancellationToken cancellationToken)
    {
        await _store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var term = query.Trim();
        if (_configuration().SelectedLibraryId == Guid.Empty || _store.Snapshot.IsReadOnly || term.Length == 0) return [];
        var generation = Volatile.Read(ref _searchGeneration);

        var uri = new Uri($"https://jable.tv/search/{Uri.EscapeDataString(term)}/");
        var html = await FetchAsync(uri, cancellationToken).ConfigureAwait(false);
        var works = _parser.ParseList(html, uri, _timeProvider.GetUtcNow()).Works;
        await EnrichAsync(works, cancellationToken).ConfigureAwait(false);
        await CacheSearchAsync(term, works, generation, cancellationToken).ConfigureAwait(false);
        return works;
    }

    public async Task<CatalogPageDto> QueryAsync(CatalogQuery query, IReadOnlyDictionary<string, Guid> localNumbers, CancellationToken cancellationToken)
    {
        await _store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var term = query.Search.Trim();
        var now = _timeProvider.GetUtcNow();
        if (_configuration().SelectedLibraryId != Guid.Empty && !_store.Snapshot.IsReadOnly && query.StartIndex == 0 && term.Length > 0
            && (!_store.Snapshot.SearchCache.TryGetValue(term, out var expires) || expires <= now))
        {
            await _searchRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
                if (_configuration().SelectedLibraryId != Guid.Empty
                    && (!_store.Snapshot.SearchCache.TryGetValue(term, out var cachedUntil) || cachedUntil <= _timeProvider.GetUtcNow()))
                {
                    try
                    {
                        if (NumberParser.ParseExact(term) is not null) await GetExactAsync(term, cancellationToken).ConfigureAwait(false);
                        else await SearchRemoteAsync(term, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsRemoteFailure(exception))
                    {
                        _failedSearchTerms[term] = 0;
                    }
                }
            }
            finally
            {
                _searchRefreshLock.Release();
            }
        }

        now = _timeProvider.GetUtcNow();
        var allowExpiredFallback = _failedSearchTerms.ContainsKey(term);
        var local = new Dictionary<string, Guid>(localNumbers, StringComparer.OrdinalIgnoreCase);
        var normalizedSearch = NumberParser.Parse(term);
        var snapshot = _store.Snapshot;
        var hits = snapshot.SearchHits.TryGetValue(term, out var matches) ? matches.ToHashSet(StringComparer.Ordinal) : [];
        var works = snapshot.Works.Values
            .Where(entry => entry.InRecent || entry.SearchExpiresAt > now || allowExpiredFallback)
            .Select(entry => entry.Work)
            .Where(work => term.Length == 0 || hits.Contains(NumberParser.IdentityKey(work.Number)) || Contains(work.Number, term) || (normalizedSearch is not null && NumberParser.IsExact(work.Number, normalizedSearch))
                || Contains(work.Title, term) || work.Actresses.Any(value => Contains(value, term))
                || work.Genres.Any(value => Contains(value, term)) || Contains(work.Studio, term))
            .Where(work => query.Source switch
            {
                CatalogSourceFilter.Local => local.ContainsKey(NumberParser.IdentityKey(work.Number)),
                CatalogSourceFilter.Online => !local.ContainsKey(NumberParser.IdentityKey(work.Number)),
                _ => true,
            })
            .Where(work => (string.IsNullOrWhiteSpace(query.Actress) || work.Actresses.Contains(query.Actress.Trim(), StringComparer.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(query.Genre) || work.Genres.Contains(query.Genre.Trim(), StringComparer.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(query.Studio) || string.Equals(work.Studio, query.Studio.Trim(), StringComparison.OrdinalIgnoreCase))
                && (!query.ReleasedFrom.HasValue || work.ReleaseDate >= query.ReleasedFrom)
                && (!query.ReleasedTo.HasValue || work.ReleaseDate <= query.ReleasedTo))
            .ToArray();

        Func<JableWork, long?> key = query.Sort switch
        {
            CatalogSortField.ViewCount => work => work.ViewCount,
            CatalogSortField.FavoriteCount => work => work.FavoriteCount,
            _ => work => work.ReleaseDate?.UtcTicks,
        };
        var ordered = works.OrderBy(work => !key(work).HasValue);
        ordered = query.Descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key);
        return new CatalogPageDto
        {
            TotalRecordCount = works.Length,
            Items = ordered.ThenBy(work => work.Number, StringComparer.OrdinalIgnoreCase)
                .Skip(query.StartIndex).Take(query.Limit).Select(work => ToDto(work, local)).ToArray(),
        };
    }

    public async Task<CatalogStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = _store.Snapshot;
        return new CatalogStatusDto
        {
            SchemaVersion = snapshot.SchemaVersion,
            LastSuccessfulSync = snapshot.LastSuccessfulSync,
            LastError = snapshot.LastError,
            IsRecovered = snapshot.IsRecovered,
            IsReadOnly = snapshot.IsReadOnly,
        };
    }

    public async Task ClearSearchAsync(CancellationToken cancellationToken)
    {
        await _searchRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.MutateAsync(snapshot =>
            {
                Interlocked.Increment(ref _searchGeneration);
                snapshot.SearchCache.Clear();
                snapshot.SearchHits.Clear();
                foreach (var key in snapshot.Works.Where(pair => !pair.Value.InRecent).Select(pair => pair.Key).ToArray()) snapshot.Works.Remove(key);
                foreach (var entry in snapshot.Works.Values) entry.SearchExpiresAt = null;
                return snapshot;
            }, cancellationToken).ConfigureAwait(false);
            _failedSearchTerms.Clear();
        }
        finally
        {
            _searchRefreshLock.Release();
        }
    }

    private async Task<string> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_configuration().SelectedLibraryId == Guid.Empty || !JableHttpClient.IsAllowedJableUri(uri))
                throw new JableRequestException(JableFailureKind.Network, "Jable access is not configured or the URI is not allowed.");
            var html = await _fetch(uri, cancellationToken).ConfigureAwait(false);
            if (JableParser.IsChallengePage(html)) throw new JableRequestException(JableFailureKind.Challenge, "Jable returned a challenge page.");
            return html;
        }
        catch (Exception exception) when (IsRemoteFailure(exception))
        {
            await RecordErrorAsync(exception.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private Task RecordErrorAsync(string message, CancellationToken cancellationToken) => _store.Snapshot.IsReadOnly ? Task.CompletedTask : _store.MutateAsync(snapshot =>
    {
        snapshot.LastError = message;
        return snapshot;
    }, cancellationToken);

    private async Task CacheSearchAsync(string term, IReadOnlyList<JableWork> works, long generation, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await _store.MutateAsync(snapshot =>
        {
            if (generation != Volatile.Read(ref _searchGeneration)) return snapshot;
            foreach (var work in works) MergeEntry(snapshot, work, detail: true).SearchExpiresAt = now.AddHours(24);
            snapshot.SearchCache[term] = now.AddHours(24);
            snapshot.SearchHits[term] = works.Select(work => NumberParser.IdentityKey(work.Number)).Distinct(StringComparer.Ordinal).ToArray();
            RemoveExpired(snapshot, now);
            snapshot.LastError = string.Empty;
            return snapshot;
        }, cancellationToken).ConfigureAwait(false);
        _failedSearchTerms.TryRemove(term, out _);
    }

    private static void RemoveExpired(CatalogSnapshot snapshot, DateTimeOffset now)
    {
        foreach (var key in snapshot.SearchCache.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) snapshot.SearchCache.Remove(key);
        foreach (var key in snapshot.Works.Where(pair => !pair.Value.InRecent && !(pair.Value.SearchExpiresAt > now)).Select(pair => pair.Key).ToArray()) snapshot.Works.Remove(key);
        CatalogStore.PruneSearchHits(snapshot);
    }

    private async Task<JableWork?> FetchDetailAsync(string number, CancellationToken cancellationToken)
    {
        await _detailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var uri = new Uri($"https://jable.tv/videos/{Uri.EscapeDataString(number.ToLowerInvariant().Replace('_', '-'))}/");
            var html = await FetchAsync(uri, cancellationToken).ConfigureAwait(false);
            return _parser.ParseDetail(html, number, uri, _timeProvider.GetUtcNow());
        }
        finally { _detailGate.Release(); }
    }

    private async Task EnrichAsync(IReadOnlyList<JableWork> works, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var batch in works.Chunk(2))
                await Task.WhenAll(batch.Select(async work =>
                {
                    try
                    {
                        if (await FetchDetailAsync(work.Number, cancellationToken).ConfigureAwait(false) is { } detail) Merge(work, detail, detail: true);
                    }
                    catch (Exception exception) when (IsRemoteFailure(exception) && exception is not JableRequestException { Kind: JableFailureKind.Challenge })
                    { /* Optional detail failure keeps the validated list work. */ }
                })).ConfigureAwait(false);
        }
        catch (JableRequestException exception) when (exception.Kind == JableFailureKind.Challenge)
        {
            // A later optional failure in this batch must not hide the challenge.
            await RecordErrorAsync(exception.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static CatalogEntry MergeEntry(CatalogSnapshot snapshot, JableWork work, bool detail)
    {
        if (!snapshot.Works.TryGetValue(work.Number, out var entry))
        {
            entry = new CatalogEntry { Work = work };
            snapshot.Works[work.Number] = entry;
        }
        else Merge(entry.Work, work, detail);
        return entry;
    }

    private static void Merge(JableWork target, JableWork source, bool detail)
    {
        if (!string.IsNullOrWhiteSpace(source.Title)) target.Title = source.Title;
        if (!string.IsNullOrWhiteSpace(source.PosterUrl)) target.PosterUrl = source.PosterUrl;
        target.ViewCount = source.ViewCount ?? target.ViewCount;
        target.FavoriteCount = source.FavoriteCount ?? target.FavoriteCount;
        if (source.FetchedAt != default) target.FetchedAt = source.FetchedAt;
        if (!string.IsNullOrWhiteSpace(source.SourcePage)) target.SourcePage = source.SourcePage;
        if (!string.IsNullOrWhiteSpace(source.CanonicalUrl) && (detail || string.IsNullOrWhiteSpace(target.CanonicalUrl))) target.CanonicalUrl = source.CanonicalUrl;
        if (!detail) return;
        if (source.Actresses.Length > 0) target.Actresses = source.Actresses;
        if (source.Genres.Length > 0) target.Genres = source.Genres;
        if (!string.IsNullOrWhiteSpace(source.Studio)) target.Studio = source.Studio;
        target.Duration = source.Duration ?? target.Duration;
        target.ReleaseDate = source.ReleaseDate ?? target.ReleaseDate;
    }

    private static bool IsRemoteFailure(Exception exception) => exception is JableRequestException or HttpRequestException;
    private static bool Contains(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);

    public static CatalogItemDto ToDto(JableWork work, IReadOnlyDictionary<string, Guid> local)
    {
        var isLocal = local.TryGetValue(NumberParser.IdentityKey(work.Number), out var id);
        return new CatalogItemDto
        {
            Number = work.Number, Title = work.Title, ReleaseDate = work.ReleaseDate, Duration = work.Duration,
            Actresses = work.Actresses, Genres = work.Genres, Studio = work.Studio,
            ViewCount = work.ViewCount, FavoriteCount = work.FavoriteCount, CanonicalUrl = work.CanonicalUrl,
            IsLocal = isLocal, JellyfinItemId = isLocal ? id : null,
            ImagePath = $"/Jable/Images/{Uri.EscapeDataString(work.Number)}",
        };
    }
}
