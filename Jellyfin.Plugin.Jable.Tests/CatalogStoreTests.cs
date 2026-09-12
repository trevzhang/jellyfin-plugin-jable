using System.Text.Json;
using Jellyfin.Plugin.Jable.Models;
using Jellyfin.Plugin.Jable.Services;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class CatalogStoreTests : IDisposable
{
    private readonly string _configurationPath = Path.Combine(Path.GetTempPath(), "JableCatalogStoreTests", Guid.NewGuid().ToString("N"));

    private string StoreDirectory => Path.Combine(_configurationPath, "Jable");
    private string CurrentPath => Path.Combine(StoreDirectory, "catalog.json");
    private string PreviousPath => Path.Combine(StoreDirectory, "catalog.previous.json");
    private string TemporaryPath => Path.Combine(StoreDirectory, "catalog.tmp");

    [Fact]
    public async Task MissingFilesLoadAnEmptySchemaOneSnapshot()
    {
        var store = new CatalogStore(_configurationPath);

        await store.EnsureLoadedAsync(CancellationToken.None);

        Assert.Equal(1, store.Snapshot.SchemaVersion);
        Assert.Empty(store.Snapshot.Works);
        Assert.Empty(store.Snapshot.SearchCache);
        Assert.False(store.Snapshot.IsRecovered);
    }

    [Fact]
    public async Task ConcurrentFirstLoadsPublishTheLoadedSnapshotToEveryCaller()
    {
        Directory.CreateDirectory(StoreDirectory);
        await File.WriteAllTextAsync(CurrentPath, JsonSerializer.Serialize(Snapshot("SSIS-123"), JsonOptions));
        var store = new CatalogStore(_configurationPath);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = Enumerable.Range(0, 32).Select(async _ =>
        {
            await start.Task;
            await store.EnsureLoadedAsync(CancellationToken.None);
            Assert.True(store.Snapshot.Works.ContainsKey("SSIS-123"));
        });

        start.SetResult();
        await Task.WhenAll(loads);
    }

    [Fact]
    public async Task ReplaceWritesValidJsonAndCleansTemporaryFile()
    {
        var store = new CatalogStore(_configurationPath);

        await store.ReplaceAsync(Snapshot("SSIS-123"), CancellationToken.None);

        var saved = await File.ReadAllTextAsync(CurrentPath);
        var parsed = JsonSerializer.Deserialize<CatalogSnapshot>(saved, JsonOptions);
        Assert.NotNull(parsed);
        Assert.True(parsed.Works.ContainsKey("SSIS-123"));
        Assert.False(File.Exists(TemporaryPath));
    }

    [Fact]
    public async Task CorruptCurrentFallsBackToPrevious()
    {
        Directory.CreateDirectory(StoreDirectory);
        await File.WriteAllTextAsync(CurrentPath, "not json");
        await File.WriteAllTextAsync(PreviousPath, JsonSerializer.Serialize(Snapshot("SSIS-123"), JsonOptions));
        var store = new CatalogStore(_configurationPath);

        await store.EnsureLoadedAsync(CancellationToken.None);

        Assert.True(store.Snapshot.Works.ContainsKey("SSIS-123"));
        Assert.True(store.Snapshot.IsRecovered);
    }

    [Fact]
    public async Task CorruptFilesLoadAnEmptyDegradedSnapshot()
    {
        Directory.CreateDirectory(StoreDirectory);
        await File.WriteAllTextAsync(CurrentPath, "not json");
        await File.WriteAllTextAsync(PreviousPath, "also not json");
        var store = new CatalogStore(_configurationPath);

        await store.EnsureLoadedAsync(CancellationToken.None);

        Assert.Empty(store.Snapshot.Works);
        Assert.True(store.Snapshot.IsRecovered);
        Assert.NotEmpty(store.Snapshot.LastError);
    }

    [Fact]
    public async Task FailedMutationKeepsPreviousCurrentFile()
    {
        var store = new CatalogStore(_configurationPath);
        await store.ReplaceAsync(Snapshot("SSIS-123"), CancellationToken.None);
        var before = await File.ReadAllTextAsync(CurrentPath);

        await Assert.ThrowsAsync<JsonException>(() => store.MutateAsync(_ => null!, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(CurrentPath));
        Assert.True(store.Snapshot.Works.ContainsKey("SSIS-123"));
        Assert.False(File.Exists(TemporaryPath));
    }

    [Fact]
    public async Task FailedWriteKeepsPreviousCurrentFile()
    {
        var store = new CatalogStore(_configurationPath);
        await store.ReplaceAsync(Snapshot("SSIS-123"), CancellationToken.None);
        var before = await File.ReadAllTextAsync(CurrentPath);
        Directory.CreateDirectory(TemporaryPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReplaceAsync(Snapshot("ABP-100"), CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(CurrentPath));
        Assert.True(store.Snapshot.Works.ContainsKey("SSIS-123"));
        Assert.False(Directory.Exists(TemporaryPath));
    }

    [Fact]
    public async Task MutationSeesCaseInsensitiveDictionaries()
    {
        var store = new CatalogStore(_configurationPath);
        var snapshot = Snapshot("SSIS-123");
        snapshot.SearchCache["Query"] = DateTimeOffset.UnixEpoch;
        await store.ReplaceAsync(snapshot, CancellationToken.None);

        await store.MutateAsync(next =>
        {
            next.Works["ssis-123"].InRecent = true;
            next.SearchCache["query"] = DateTimeOffset.UnixEpoch.AddDays(1);
            return next;
        }, CancellationToken.None);

        Assert.Single(store.Snapshot.Works);
        Assert.True(store.Snapshot.Works["SSIS-123"].InRecent);
        Assert.Single(store.Snapshot.SearchCache);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), store.Snapshot.SearchCache["QUERY"]);
    }

    [Fact]
    public async Task ClearExpiredRemovesOnlyExpiredOnDemandRecords()
    {
        var now = new DateTimeOffset(2026, 09, 12, 12, 00, 00, TimeSpan.Zero);
        var snapshot = Snapshot("RECENT-1");
        snapshot.Works["RECENT-1"].InRecent = true;
        snapshot.Works["RECENT-1"].SearchExpiresAt = now.AddDays(-1);
        snapshot.Works["EXPIRED-1"] = new CatalogEntry { Work = new JableWork { Number = "EXPIRED-1", Title = "Expired", CanonicalUrl = "https://jable.tv/videos/expired-1/" }, SearchExpiresAt = now.AddMinutes(-1) };
        snapshot.Works["FRESH-1"] = new CatalogEntry { Work = new JableWork { Number = "FRESH-1", Title = "Fresh", CanonicalUrl = "https://jable.tv/videos/fresh-1/" }, SearchExpiresAt = now.AddMinutes(1) };
        snapshot.SearchCache["expired"] = now.AddMinutes(-1);
        snapshot.SearchCache["fresh"] = now.AddMinutes(1);
        var store = new CatalogStore(_configurationPath);
        await store.ReplaceAsync(snapshot, CancellationToken.None);

        await store.ClearExpiredSearchEntriesAsync(now, CancellationToken.None);

        Assert.True(store.Snapshot.Works.ContainsKey("recent-1"));
        Assert.False(store.Snapshot.Works.ContainsKey("expired-1"));
        Assert.True(store.Snapshot.Works.ContainsKey("fresh-1"));
        Assert.False(store.Snapshot.SearchCache.ContainsKey("expired"));
        Assert.True(store.Snapshot.SearchCache.ContainsKey("fresh"));
    }

    [Fact]
    public async Task LoadedDictionariesAreCaseInsensitive()
    {
        Directory.CreateDirectory(StoreDirectory);
        await File.WriteAllTextAsync(CurrentPath, """
            {"schemaVersion":1,"works":{"ssis-123":{"work":{"number":"SSIS-123","title":"Example","canonicalUrl":"https://jable.tv/videos/ssis-123/"}}},"searchCache":{"query":"2026-09-13T00:00:00+00:00"}}
            """);
        var store = new CatalogStore(_configurationPath);

        await store.EnsureLoadedAsync(CancellationToken.None);

        Assert.True(store.Snapshot.Works.ContainsKey("SSIS-123"));
        Assert.True(store.Snapshot.SearchCache.ContainsKey("QUERY"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_configurationPath))
        {
            Directory.Delete(_configurationPath, true);
        }
    }

    private static CatalogSnapshot Snapshot(string number) => new()
    {
        Works = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [number] = new() { Work = new JableWork { Number = number, Title = number, CanonicalUrl = $"https://jable.tv/videos/{number.ToLowerInvariant()}/" } }
        }
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
