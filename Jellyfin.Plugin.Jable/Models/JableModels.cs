#pragma warning disable CS1591

namespace Jellyfin.Plugin.Jable.Models;

public sealed class JableWork
{
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string PosterUrl { get; set; } = string.Empty;
    public DateTimeOffset? ReleaseDate { get; set; }
    public TimeSpan? Duration { get; set; }
    public string[] Actresses { get; set; } = [];
    public string[] Genres { get; set; } = [];
    public string Studio { get; set; } = string.Empty;
    public long? ViewCount { get; set; }
    public long? FavoriteCount { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public string SourcePage { get; set; } = string.Empty;
}

public sealed class JableListPage
{
    public IReadOnlyList<JableWork> Works { get; set; } = [];
    public Uri? NextPageUri { get; set; }
    public bool HasNextLink { get; set; }
}

public sealed class CatalogEntry
{
    public JableWork Work { get; set; } = new();
    public bool InRecent { get; set; }
    public DateTimeOffset? SearchExpiresAt { get; set; }
}

public sealed class CatalogSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, CatalogEntry> Works { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> SearchCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> SearchHits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastSuccessfulSync { get; set; }
    public string LastError { get; set; } = string.Empty;
    public bool IsRecovered { get; set; }
    public bool IsReadOnly { get; set; }
}

public enum CatalogSourceFilter { All, Local, Online }

public enum CatalogSortField { ReleaseDate, ViewCount, FavoriteCount }

public sealed class CatalogQuery
{
    private int _limit = 50;
    private int _startIndex;

    public string Search { get; set; } = string.Empty;
    public CatalogSourceFilter Source { get; set; } = CatalogSourceFilter.All;
    public string Actress { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Studio { get; set; } = string.Empty;
    public DateTimeOffset? ReleasedFrom { get; set; }
    public DateTimeOffset? ReleasedTo { get; set; }
    public CatalogSortField Sort { get; set; } = CatalogSortField.ReleaseDate;
    public bool Descending { get; set; } = true;
    public int StartIndex
    {
        get => _startIndex;
        set => _startIndex = Math.Max(0, value);
    }
    public int Limit
    {
        get => _limit;
        set => _limit = Math.Clamp(value, 1, 100);
    }
}

public sealed class CatalogPageDto
{
    public int ApiVersion { get; set; } = 1;
    public CatalogItemDto[] Items { get; set; } = [];
    public int TotalRecordCount { get; set; }
}

public sealed class CatalogItemDto
{
    public int ApiVersion { get; set; } = 1;
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset? ReleaseDate { get; set; }
    public TimeSpan? Duration { get; set; }
    public string[] Actresses { get; set; } = [];
    public string[] Genres { get; set; } = [];
    public string Studio { get; set; } = string.Empty;
    public long? ViewCount { get; set; }
    public long? FavoriteCount { get; set; }
    public bool IsLocal { get; set; }
    public Guid? JellyfinItemId { get; set; }
    public string CanonicalUrl { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
}

public sealed class CatalogStatusDto
{
    public int ApiVersion { get; set; } = 1;
    public int SchemaVersion { get; set; }
    public DateTimeOffset? LastSuccessfulSync { get; set; }
    public string LastError { get; set; } = string.Empty;
    public bool IsRecovered { get; set; }
    public bool IsReadOnly { get; set; }
}

public enum JableFailureKind
{
    None,
    Network,
    Challenge,
    Parse,
    IdentityMismatch,
}
