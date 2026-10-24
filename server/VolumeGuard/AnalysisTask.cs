using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VolumeGuard;

/// <summary>Opt-in whole-file loudness measurements; never scans live or remote streams.</summary>
public sealed class AnalysisTask(ILibraryManager library, AnalysisService analysis, ILogger<AnalysisTask> logger) : IScheduledTask
{
    public string Name => "Volume Guard loudness analysis";
    public string Key => "VolumeGuardLoudnessAnalysis";
    public string Description => "Measure local movie and episode audio tracks for title-to-title loudness correction. Enable analysis in Volume Guard settings first. Processes one track at a time.";
    public string Category => "Volume Guard";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks, MaxRuntimeTicks = TimeSpan.FromHours(6).Ticks }];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        lock (Plugin.ConfigurationLock) if (!Plugin.Instance.Configuration.LoudnessAnalysisEnabled) { progress.Report(100); return; }
        if (!analysis.TryBeginScan()) return;
        var completed = false;
        try
        {
            const int batchSize = 100;
            var offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (Plugin.ConfigurationLock) if (!Plugin.Instance.Configuration.LoudnessAnalysisEnabled) return;
                var items = library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                    IsFolder = false, Recursive = true, IsVirtualItem = false,
                    SourceTypes = [SourceType.Library], StartIndex = offset, Limit = batchSize,
                    OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
                    DtoOptions = new DtoOptions(false) { EnableImages = false }
                });
                if (items.Count == 0) break;
                foreach (var item in items.OfType<Video>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (Plugin.ConfigurationLock) if (!Plugin.Instance.Configuration.LoudnessAnalysisEnabled) return;
                    try
                    {
                        foreach (var source in item.GetMediaSources(enablePathSubstitution: false))
                        {
                            if (source.Protocol != MediaProtocol.File || string.IsNullOrWhiteSpace(source.Path) || !File.Exists(source.Path)) continue;
                            foreach (var stream in source.MediaStreams.Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal && s.Index >= 0))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                lock (Plugin.ConfigurationLock) if (!Plugin.Instance.Configuration.LoudnessAnalysisEnabled) return;
                                await analysis.AnalyzeAsync(source.Path, stream.Index, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        analysis.ReportFailure("A library item could not be analyzed. See server logs.");
                        logger.LogWarning(e, "Volume Guard skipped unavailable library item {ItemId}", item.Id);
                    }
                }
                offset += items.Count;
                // The library can change during a scan. Intermediate progress is an estimate.
                var percent = 100.0 * offset / (offset + (items.Count == batchSize ? batchSize : 0));
                analysis.ReportProgress(percent); progress.Report(percent);
                if (items.Count < batchSize) break;
            }
            analysis.ReportProgress(100); progress.Report(100); completed = true;
        }
        finally { analysis.EndScan(completed); }
    }
}
