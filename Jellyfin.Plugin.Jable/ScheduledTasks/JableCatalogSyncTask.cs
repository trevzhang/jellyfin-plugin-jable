using Jellyfin.Plugin.Jable.Services;
using MediaBrowser.Model.Tasks;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.Jable.ScheduledTasks;

public sealed class JableCatalogSyncTask(JableCatalogService catalog) : IScheduledTask
{
    public string Name => "Sync Jable catalog";
    public string Key => "JableCatalogSync";
    public string Description => "Synchronize recent Jable works for the selected library.";
    public string Category => "Jable";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerInterval,
            IntervalTicks = TimeSpan.FromHours(12).Ticks,
        },
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return catalog.SyncRecentAsync(progress, cancellationToken);
    }
}
