using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jable.Configuration;
using Jellyfin.Plugin.Jable.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Jable.Tests;

public sealed class LibraryAccessTests
{
    private static readonly Guid Selected = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly PluginConfiguration _config = new() { SelectedLibraryId = Selected };
    private readonly User _user = new("viewer", "auth", "reset");

    [Fact]
    public void AccessRequiresSelectedLibraryAndAnAllowedPolicy()
    {
        Assert.False(LibraryAccessService.CanAccess(Guid.Empty, new UserPolicy { IsAdministrator = true }));
        Assert.False(LibraryAccessService.CanAccess(Selected, null));
        Assert.True(LibraryAccessService.CanAccess(Selected, new UserPolicy { IsAdministrator = true }));
        Assert.True(LibraryAccessService.CanAccess(Selected, new UserPolicy { EnableAllFolders = true }));
        Assert.True(LibraryAccessService.CanAccess(Selected, new UserPolicy { EnableAllFolders = false, EnabledFolders = [Selected] }));
        Assert.False(LibraryAccessService.CanAccess(Selected, new UserPolicy { EnableAllFolders = false, EnabledFolders = [Guid.NewGuid()] }));
        Assert.False(LibraryAccessService.CanAccess(Selected, new UserPolicy { EnableAllFolders = false, EnabledFolders = null! }));
    }

    [Fact]
    public void BlockedSelectedLibraryOverridesAllFoldersAndPreventsQuery()
    {
        var policy = new UserPolicy { EnableAllFolders = true, BlockedMediaFolders = [Selected] };
        Assert.False(LibraryAccessService.CanAccess(Selected, policy));
        var service = Create(policy: policy, query: _ => throw new Exception("Blocked library query"));
        Assert.False(service.CanAccess(_user));
        Assert.Empty(service.BuildLocalNumberIndex(_user));
    }

    [Fact]
    public void NonemptyBlockedListAllowsAnUnblockedLibraryWithoutEnabledFolders()
    {
        var policy = new UserPolicy { EnableAllFolders = false, EnabledFolders = [], BlockedMediaFolders = [Guid.NewGuid()] };
        Assert.True(LibraryAccessService.CanAccess(Selected, policy));
        policy.EnabledFolders = null!;
        Assert.True(LibraryAccessService.CanAccess(Selected, policy));
    }

    [Fact]
    public void EmptyAndNullBlockedListsFallBackToExplicitFolderGrants()
    {
        var policy = new UserPolicy { EnableAllFolders = false, EnabledFolders = null!, BlockedMediaFolders = null! };
        Assert.False(LibraryAccessService.CanAccess(Selected, policy));
        policy.BlockedMediaFolders = [];
        Assert.False(LibraryAccessService.CanAccess(Selected, policy));
        policy.EnabledFolders = [Selected];
        Assert.True(LibraryAccessService.CanAccess(Selected, policy));
        policy.BlockedMediaFolders = null!;
        Assert.True(LibraryAccessService.CanAccess(Selected, policy));
    }

    [Fact]
    public void AdministratorBypassesBlockedListOnlyWhenALibraryIsSelected()
    {
        var policy = new UserPolicy { IsAdministrator = true, EnableAllFolders = false, BlockedMediaFolders = [Selected] };
        Assert.True(LibraryAccessService.CanAccess(Selected, policy));
        Assert.False(LibraryAccessService.CanAccess(Guid.Empty, policy));
    }

    [Fact]
    public void NullAndUnauthorizedUsersNeverQueryMovies()
    {
        var service = Create(policy: new UserPolicy { EnableAllFolders = false }, query: _ => throw new Exception("Unauthorized query"));
        Assert.False(service.CanAccess(null));
        Assert.Empty(service.BuildLocalNumberIndex(null));
        Assert.False(service.CanAccess(_user));
        Assert.Empty(service.BuildLocalNumberIndex(_user));
        _config.SelectedLibraryId = Guid.Empty;
        Assert.Empty(Create(query: _ => throw new Exception("Unconfigured query")).BuildLocalNumberIndex(_user));
    }

    [Fact]
    public void ItemScopeUsesCollectionIdsAndTracksConfigurationChanges()
    {
        var item = new Movie();
        var service = Create(collections: candidate =>
        {
            Assert.Same(item, candidate);
            return [new Folder { Id = Selected }];
        });
        Assert.True(service.IsSelectedLibraryItem(item));
        _config.SelectedLibraryId = Guid.NewGuid();
        Assert.False(service.IsSelectedLibraryItem(item));
        _config.SelectedLibraryId = Guid.Empty;
        Assert.False(service.IsSelectedLibraryItem(item));
    }

    [Theory]
    [InlineData("/media/porn", true)]
    [InlineData("/media/porn/", true)]
    [InlineData("/media/porn/movie/file.mp4", true)]
    [InlineData("/media/porn/../porn/movie.mp4", true)]
    [InlineData("/media/porn-old/movie.mp4", false)]
    [InlineData("/media/porn/../movie.mp4", false)]
    [InlineData("/media/porn/sub/../../movie.mp4", false)]
    [InlineData("/media", false)]
    [InlineData("/other/movie.mp4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("/media/porn/invalid\0.mp4", false)]
    public void PathScopeRejectsSiblingAndParentEscapes(string? candidate, bool expected)
    {
        var service = Create(folders: () => [new VirtualFolderInfo { ItemId = Selected.ToString("N"), Locations = ["/media/porn/"] }]);
        Assert.Equal(expected, service.IsSelectedLibraryPath(candidate));
    }

    [Fact]
    public void PathScopeUsesOnlyExactSelectedFolderAndPlatformCasing()
    {
        var service = Create(folders: () =>
        [
            new VirtualFolderInfo { ItemId = "invalid", Locations = ["/wrong"] },
            new VirtualFolderInfo { ItemId = Selected.ToString() + "extra", Locations = ["/suffix"] },
            new VirtualFolderInfo { ItemId = Guid.NewGuid().ToString(), Locations = ["/other"] },
            new VirtualFolderInfo { ItemId = Selected.ToString(), Locations = ["bad\0root", "/media/porn/", "/second"] },
        ]);
        Assert.True(service.IsSelectedLibraryPath("/second/movie.mp4"));
        Assert.False(service.IsSelectedLibraryPath("/wrong/movie.mp4"));
        Assert.False(service.IsSelectedLibraryPath("/suffix/movie.mp4"));
        Assert.False(service.IsSelectedLibraryPath("/other/movie.mp4"));
        Assert.Equal(OperatingSystem.IsWindows(), service.IsSelectedLibraryPath("/MEDIA/PORN/movie.mp4"));
        _config.SelectedLibraryId = Guid.Empty;
        Assert.False(service.IsSelectedLibraryPath("/media/porn/movie.mp4"));
    }

    [Fact]
    public void IndexUsesUserMovieAncestorScopeAndStableFirstIdentityWithFallbacks()
    {
        _user.MaxParentalAgeRating = 18;
        var first = new Movie { Id = Guid.Parse("00000001-0000-0000-0000-000000000000"), Name = "WRONG-999", Path = "/media/OTHER-111.mp4", ProviderIds = new() { ["Jable"] = "ssis-123" } };
        var duplicate = new Movie { Id = Guid.Parse("00000002-0000-0000-0000-000000000000"), Name = "SSIS123" };
        var name = new Movie { Id = Guid.NewGuid(), Name = "ABP-001 title", Path = "/media/OTHER-222.mp4", ProviderIds = new() { ["Jable"] = "invalid" } };
        var path = new Movie { Id = Guid.NewGuid(), Name = "no number", Path = "/media/FC2-PPV-1234567.mp4" };
        var service = Create(query: query =>
        {
            Assert.Same(_user, query.User);
            Assert.Equal(18, query.MaxParentalRating);
            Assert.Equal(new[] { Selected }, query.AncestorIds);
            Assert.Equal(new[] { BaseItemKind.Movie }, query.IncludeItemTypes);
            Assert.True(query.Recursive);
            return [duplicate, new Folder { Name = "FOLDER-123" }, path, first, name, new Movie { Name = "no number" }];
        });
        var index = service.BuildLocalNumberIndex(_user);
        Assert.Equal(3, index.Count);
        Assert.Equal(first.Id, index["SSIS123"]);
        Assert.Equal(name.Id, index["ABP001"]);
        Assert.Equal(path.Id, index["FC2PPV1234567"]);
    }

    [Fact]
    public void AccessServiceIsRegisteredAsSingleton()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        var registration = Assert.Single(services, x => x.ServiceType == typeof(LibraryAccessService));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        Assert.Equal(typeof(LibraryAccessService), registration.ImplementationType);
    }

    private LibraryAccessService Create(UserPolicy? policy = null,
        Func<BaseItem, IEnumerable<Folder>>? collections = null,
        Func<IEnumerable<VirtualFolderInfo>>? folders = null,
        Func<InternalItemsQuery, IEnumerable<BaseItem>>? query = null) =>
        new(() => _config, user => { Assert.Same(_user, user); return policy ?? new UserPolicy { EnableAllFolders = true }; },
            collections ?? (_ => []), folders ?? (() => []), query ?? (_ => []));
}
