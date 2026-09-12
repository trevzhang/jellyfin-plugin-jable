using System.Text.Json;
using Jellyfin.Plugin.Jable.Models;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Jable.Services;

/// <summary>Persists the catalog as an atomically replaced JSON snapshot.</summary>
public sealed class CatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _currentPath;
    private readonly string _previousPath;
    private readonly string _temporaryPath;
    private CatalogSnapshot _snapshot = EmptySnapshot();
    private bool _loaded;

    /// <summary>Initializes a store in Jellyfin's plugin configuration directory.</summary>
    /// <param name="applicationPaths">Jellyfin's application paths.</param>
    public CatalogStore(IApplicationPaths applicationPaths)
        : this(applicationPaths.PluginConfigurationsPath)
    {
    }

    /// <summary>Initializes a store below an explicit configuration directory.</summary>
    /// <param name="pluginConfigurationsPath">A testable replacement for Jellyfin's plugin configuration directory.</param>
    public CatalogStore(string pluginConfigurationsPath)
    {
        var directory = Path.Combine(pluginConfigurationsPath, "Jable");
        _currentPath = Path.Combine(directory, "catalog.json");
        _previousPath = Path.Combine(directory, "catalog.previous.json");
        _temporaryPath = Path.Combine(directory, "catalog.tmp");
    }

    /// <summary>Gets the currently loaded catalog snapshot.</summary>
    public CatalogSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Loads the current snapshot, or its previous valid version when recovery is needed.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when loading is finished.</returns>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _loaded))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _loaded))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_currentPath)!);
            var current = await ReadSnapshotAsync(_currentPath, cancellationToken).ConfigureAwait(false);
            if (current.Snapshot is not null)
            {
                Volatile.Write(ref _snapshot, Normalize(current.Snapshot));
            }
            else
            {
                var previous = await ReadSnapshotAsync(_previousPath, cancellationToken).ConfigureAwait(false);
                Volatile.Write(
                    ref _snapshot,
                    previous.Snapshot is null
                        ? EmptySnapshot(current.Exists || previous.Exists, "Catalog snapshots are invalid; read-only until an administrator restores or moves the damaged files while Jellyfin is stopped.")
                        : MarkRecovered(Normalize(previous.Snapshot)));
            }

            Volatile.Write(ref _loaded, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces the catalog with a persisted copy of the supplied snapshot.</summary>
    /// <param name="snapshot">Snapshot to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when replacement is persisted.</returns>
    public Task ReplaceAsync(CatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return MutateAsync(_ => Clone(snapshot), cancellationToken);
    }

    /// <summary>Applies and atomically persists a mutation of the catalog.</summary>
    /// <param name="mutation">Mutation to apply to a detached snapshot copy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the mutation is persisted.</returns>
    public async Task MutateAsync(Func<CatalogSnapshot, CatalogSnapshot> mutation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.IsReadOnly) throw new InvalidOperationException("Catalog is read-only because both snapshots are invalid.");
            var next = Normalize(mutation(Normalize(Clone(Volatile.Read(ref _snapshot)))) ?? throw new JsonException("Snapshot mutation returned null."));
            await WriteSnapshotAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes expired search cache entries and their no-longer-recent works.</summary>
    /// <param name="now">The time used to decide expiration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the cleanup is persisted.</returns>
    public Task ClearExpiredSearchEntriesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        return MutateAsync(snapshot =>
        {
            foreach (var key in snapshot.SearchCache.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            {
                snapshot.SearchCache.Remove(key);
                snapshot.SearchHits.Remove(key);
            }

            foreach (var key in snapshot.Works.Where(pair => !pair.Value.InRecent && pair.Value.SearchExpiresAt <= now).Select(pair => pair.Key).ToArray())
            {
                snapshot.Works.Remove(key);
            }

            PruneSearchHits(snapshot);
            return snapshot;
        }, cancellationToken);
    }

    private async Task WriteSnapshotAsync(CatalogSnapshot next, CancellationToken cancellationToken)
    {
        try
        {
            await using (var stream = new FileStream(
                _temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, next, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var checkedSnapshot = (await ReadSnapshotAsync(_temporaryPath, cancellationToken).ConfigureAwait(false)).Snapshot
                ?? throw new JsonException("Snapshot validation returned null.");
            next = Normalize(checkedSnapshot);

            if (File.Exists(_currentPath) && (await ReadSnapshotAsync(_currentPath, cancellationToken).ConfigureAwait(false)).Snapshot is not null)
            {
                File.Copy(_currentPath, _previousPath, true);
            }

            File.Move(_temporaryPath, _currentPath, true);
            Volatile.Write(ref _snapshot, next);
        }
        finally
        {
            if (File.Exists(_temporaryPath))
            {
                File.Delete(_temporaryPath);
            }
            else if (Directory.Exists(_temporaryPath))
            {
                Directory.Delete(_temporaryPath);
            }
        }
    }

    private static CatalogSnapshot Clone(CatalogSnapshot snapshot)
    {
        return JsonSerializer.Deserialize<CatalogSnapshot>(JsonSerializer.Serialize(snapshot, JsonOptions), JsonOptions)
            ?? throw new JsonException("Snapshot clone returned null.");
    }

    private static CatalogSnapshot Normalize(CatalogSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.Works is null || snapshot.SearchCache is null || snapshot.SearchHits is null)
            throw new JsonException("Unsupported or invalid catalog structure.");
        snapshot.Works = new Dictionary<string, CatalogEntry>(snapshot.Works, StringComparer.OrdinalIgnoreCase);
        snapshot.SearchCache = new Dictionary<string, DateTimeOffset>(snapshot.SearchCache, StringComparer.OrdinalIgnoreCase);
        snapshot.SearchHits = new Dictionary<string, string[]>(snapshot.SearchHits, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in snapshot.Works)
        {
            var work = entry?.Work;
            if (work is null || NumberParser.ParseExact(work.Number) is not { } number || NumberParser.IdentityKey(key) != NumberParser.IdentityKey(number)
                || string.IsNullOrWhiteSpace(work.Title) || !Uri.TryCreate(work.CanonicalUrl, UriKind.Absolute, out var canonical)
                || JableParser.CanonicalNumber(canonical) is not { } canonicalNumber || !NumberParser.IsExact(number, canonicalNumber))
                throw new JsonException("Invalid catalog work identity or required fields.");
            work.Number = number;
            work.Title = work.Title.Trim();
            work.PosterUrl ??= string.Empty;
            work.SourcePage ??= string.Empty;
            work.Studio ??= string.Empty;
            work.Actresses = (work.Actresses ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            work.Genres = (work.Genres ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        snapshot.Works = snapshot.Works.Values.ToDictionary(entry => entry.Work.Number, StringComparer.OrdinalIgnoreCase);
        var identities = snapshot.Works.Values.Select(entry => NumberParser.IdentityKey(entry.Work.Number)).ToHashSet(StringComparer.Ordinal);
        foreach (var (term, hits) in snapshot.SearchHits)
        {
            if (string.IsNullOrWhiteSpace(term) || !snapshot.SearchCache.ContainsKey(term) || hits is null || hits.Any(hit => hit is null || !identities.Contains(hit)))
                throw new JsonException("Invalid catalog search hits.");
        }
        snapshot.LastError ??= string.Empty;
        return snapshot;
    }

    internal static void PruneSearchHits(CatalogSnapshot snapshot)
    {
        var identities = snapshot.Works.Values.Select(entry => NumberParser.IdentityKey(entry.Work.Number)).ToHashSet(StringComparer.Ordinal);
        foreach (var term in snapshot.SearchHits.Keys.ToArray())
        {
            if (!snapshot.SearchCache.ContainsKey(term)) snapshot.SearchHits.Remove(term);
            else snapshot.SearchHits[term] = snapshot.SearchHits[term].Where(identities.Contains).Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    private static CatalogSnapshot MarkRecovered(CatalogSnapshot snapshot)
    {
        snapshot.IsRecovered = true;
        return snapshot;
    }

    private static CatalogSnapshot EmptySnapshot(bool degraded = false, string error = "") => new()
    {
        IsRecovered = degraded,
        IsReadOnly = degraded,
        LastError = degraded ? error : string.Empty,
    };

    private static async Task<(CatalogSnapshot? Snapshot, bool Exists)> ReadSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return (null, false);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync<CatalogSnapshot>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return (snapshot is null ? null : Normalize(snapshot), true);
        }
        catch (JsonException)
        {
            return (null, true);
        }
        catch (ArgumentException)
        {
            return (null, true); // Conflicting case-insensitive or normalized keys are structural corruption.
        }
        catch (IOException)
        {
            return (null, true);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, true);
        }
    }
}
