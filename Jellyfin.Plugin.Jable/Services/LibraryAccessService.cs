using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jable.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.Jable.Services;

public sealed class LibraryAccessService
{
    private readonly Func<PluginConfiguration> _configuration;
    private readonly Func<User, UserPolicy?> _policy;
    private readonly Func<BaseItem, IEnumerable<Folder>> _collections;
    private readonly Func<IEnumerable<VirtualFolderInfo>> _folders;
    private readonly Func<InternalItemsQuery, IEnumerable<BaseItem>> _query;

    public LibraryAccessService(ILibraryManager libraryManager, IUserManager userManager)
        : this(() => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            user => userManager.GetUserDto(user).Policy, libraryManager.GetCollectionFolders,
            libraryManager.GetVirtualFolders, libraryManager.GetItemList)
    {
    }

    public LibraryAccessService(Func<PluginConfiguration> configuration, Func<User, UserPolicy?> policy,
        Func<BaseItem, IEnumerable<Folder>> collections, Func<IEnumerable<VirtualFolderInfo>> folders,
        Func<InternalItemsQuery, IEnumerable<BaseItem>> query)
    {
        _configuration = configuration;
        _policy = policy;
        _collections = collections;
        _folders = folders;
        _query = query;
    }

    public static bool CanAccess(Guid selectedLibraryId, UserPolicy? policy) =>
        selectedLibraryId != Guid.Empty && policy is not null
        && (policy.IsAdministrator || (policy.BlockedMediaFolders is { Length: > 0 } blocked
            ? !blocked.Contains(selectedLibraryId)
            : policy.EnableAllFolders || policy.EnabledFolders?.Contains(selectedLibraryId) == true));

    public bool CanAccess(User? user)
    {
        var selected = _configuration().SelectedLibraryId;
        return selected != Guid.Empty && user is not null && CanAccess(selected, _policy(user));
    }

    public bool IsSelectedLibraryItem(BaseItem item)
    {
        var selected = _configuration().SelectedLibraryId;
        return selected != Guid.Empty && _collections(item).Any(folder => folder.Id == selected);
    }

    public bool IsSelectedLibraryPath(string? path)
    {
        var selected = _configuration().SelectedLibraryId;
        if (selected == Guid.Empty || string.IsNullOrWhiteSpace(path)) return false;
        return _folders()
            .Where(folder => Guid.TryParse(folder.ItemId, out var id) && id == selected)
            .SelectMany(folder => folder.Locations ?? [])
            .Any(root => IsWithinPath(root, path));
    }

    private static bool IsWithinPath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return !Path.IsPathRooted(relative) && !string.Equals(relative, "..", comparison)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, comparison)
                // GetRelativePath also ignores case on macOS; enforce the plugin's Unix ordinal rule.
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(fullRoot, relative))),
                    Path.TrimEndingDirectorySeparator(fullPath), comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    public Dictionary<string, Guid> BuildLocalNumberIndex(User? user)
    {
        var selected = _configuration().SelectedLibraryId;
        var index = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (selected == Guid.Empty || user is null || !CanAccess(selected, _policy(user))) return index;

        var query = new InternalItemsQuery(user)
        {
            AncestorIds = [selected],
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
        };
        foreach (var movie in _query(query).OfType<Movie>().OrderBy(movie => movie.Id))
        {
            movie.ProviderIds.TryGetValue("Jable", out var providerId);
            var number = NumberParser.Parse(providerId) ?? NumberParser.Parse(movie.Name) ?? NumberParser.Parse(movie.Path);
            if (number is not null) index.TryAdd(NumberParser.IdentityKey(number), movie.Id);
        }

        return index;
    }
}
