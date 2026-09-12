using System.Net.Http;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jable.Models;
using Jellyfin.Plugin.Jable.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.Jable.Providers;

public sealed class JableMovieProvider(JableCatalogService catalog, JableHttpClient client, LibraryAccessService access)
    : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    public string Name => "Jable";
    public int Order => 0;

    public static Movie Map(JableWork work) => new()
    {
        Name = JableProviderLookup.DisplayName(work),
        OriginalTitle = string.IsNullOrWhiteSpace(work.Title) ? null : work.Title,
        ProviderIds = new() { ["Jable"] = work.Number },
        ForcedSortName = work.Number,
        OfficialRating = "XXX",
        PremiereDate = work.ReleaseDate?.UtcDateTime,
        ProductionYear = work.ReleaseDate?.Year,
        Genres = work.Genres.ToArray(),
        Studios = string.IsNullOrWhiteSpace(work.Studio) ? [] : [work.Studio],
        RunTimeTicks = work.Duration?.Ticks,
    };

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Movie>();
        if (!access.IsSelectedLibraryPath(info.Path)) return result;
        cancellationToken.ThrowIfCancellationRequested();
        var number = JableProviderLookup.Number(info.ProviderIds, info.Name, info.Path);
        if (number is null) return result;
        var work = await JableProviderLookup.ExactAsync(catalog, number, cancellationToken).ConfigureAwait(false);
        if (work is null) return result;

        result.Item = Map(work);
        result.HasMetadata = true;
        result.QueriedById = true;
        foreach (var actress in work.Actresses.Where(name => !string.IsNullOrWhiteSpace(name)))
            result.AddPerson(new PersonInfo { Name = actress, Type = PersonKind.Actor });
        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var number = JableProviderLookup.Number(searchInfo.ProviderIds, searchInfo.Name, searchInfo.Path);
        IReadOnlyList<JableWork> works;
        if (number is not null)
        {
            var work = await JableProviderLookup.ExactAsync(catalog, number, cancellationToken).ConfigureAwait(false);
            works = work is null ? [] : [work];
        }
        else
        {
            try
            {
                works = await catalog.SearchRemoteAsync(searchInfo.Name ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JableRequestException or HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return [];
            }
        }

        return works.Select(work => new RemoteSearchResult
        {
            Name = JableProviderLookup.DisplayName(work),
            ProviderIds = new() { ["Jable"] = work.Number },
            SearchProviderName = Name,
            PremiereDate = work.ReleaseDate?.UtcDateTime,
            ProductionYear = work.ReleaseDate?.Year,
            ImageUrl = JableProviderLookup.PosterUri(work.PosterUrl)?.AbsoluteUri,
        }).ToArray();
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        JableProviderLookup.ImageAsync(client, url, cancellationToken);
}

public sealed class JableImageProvider(JableCatalogService catalog, JableHttpClient client, LibraryAccessService access)
    : IRemoteImageProvider, IHasOrder
{
    public string Name => "Jable";
    public int Order => 0;
    public bool Supports(BaseItem item) => item is Movie;
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => Supports(item) ? [ImageType.Primary] : [];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (!Supports(item) || !access.IsSelectedLibraryItem(item)) return [];
        cancellationToken.ThrowIfCancellationRequested();
        var number = JableProviderLookup.Number(item.ProviderIds, item.Name, item.Path);
        if (number is null) return [];
        var work = await JableProviderLookup.ExactAsync(catalog, number, cancellationToken).ConfigureAwait(false);
        if (work is null || JableProviderLookup.PosterUri(work.PosterUrl) is not { } uri) return [];
        return [new RemoteImageInfo { ProviderName = Name, Type = ImageType.Primary, Url = uri.AbsoluteUri }];
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        JableProviderLookup.ImageAsync(client, url, cancellationToken);
}

internal static class JableProviderLookup
{
    internal static string DisplayName(JableWork work)
    {
        var title = work.Title.Trim();
        if (title.Length == 0) return work.Number;
        var words = title.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
        return Enumerable.Range(1, Math.Min(3, words.Length)).Any(count => NumberParser.IsExact(string.Join(' ', words.Take(count)), work.Number))
            ? title : $"{work.Number} {title}";
    }

    internal static string? Number(Dictionary<string, string> providerIds, string? name, string? path)
    {
        providerIds.TryGetValue("Jable", out var providerId);
        return NumberParser.Parse(providerId) ?? NumberParser.Parse(name) ?? NumberParser.Parse(path);
    }

    internal static async Task<JableWork?> ExactAsync(JableCatalogService catalog, string number, CancellationToken cancellationToken)
    {
        try
        {
            var work = await catalog.GetExactAsync(number, cancellationToken).ConfigureAwait(false);
            return work is not null && NumberParser.IsExact(number, work.Number) ? work : null;
        }
        catch (Exception exception) when (exception is JableRequestException or HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    internal static Uri? PosterUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && JableHttpClient.IsAllowedJableUri(uri) ? uri : null;

    internal static async Task<HttpResponseMessage> ImageAsync(JableHttpClient client, string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = PosterUri(url) ?? throw new JableRequestException(JableFailureKind.Network, "Jable image URI is not allowed.");
        return await client.GetImageAsync(uri, cancellationToken).ConfigureAwait(false);
    }
}
