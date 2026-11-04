using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource.AniList;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Commands;

namespace NzbDrone.Core.AnimeSite
{
    public class SiteSeriesSyncService : IExecute<SiteSeriesSyncCommand>
    {
        // Downloads started per run. Each one resolves its sources first, and
        // the downloader runs only a few at a time anyway; whatever is left
        // is picked up by the next run.
        private const int MaxDownloadsPerRun = 30;

        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly ISiteShowService _siteShowService;
        private readonly ISiteDownloadService _siteDownloadService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public SiteSeriesSyncService(ISeriesService seriesService,
                                     IEpisodeService episodeService,
                                     ISiteShowService siteShowService,
                                     ISiteDownloadService siteDownloadService,
                                     IManageCommandQueue commandQueueManager,
                                     Logger logger)
        {
            _seriesService = seriesService;
            _episodeService = episodeService;
            _siteShowService = siteShowService;
            _siteDownloadService = siteDownloadService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Execute(SiteSeriesSyncCommand message)
        {
            var aniListSeries = _seriesService.GetAllSeries()
                .Where(s => s.Monitored && (AniListSeriesIds.IsAniListId(s.TvdbId) || SiteSeriesIds.IsSiteId(s.TvdbId)))
                .ToList();

            if (aniListSeries.Count == 0)
            {
                _logger.Debug("No monitored Site/AniList-backed series to sync");
                return;
            }

            var now = DateTime.UtcNow;

            // Refresh only the series that haven't been refreshed recently
            // -- Sonarr's own 12h refresh already covers the rest, and
            // re-fetching every synthetic series every 6h hammers AniList.
            var staleIds = aniListSeries
                .Where(s => s.LastInfoSync == null || now - s.LastInfoSync.Value > TimeSpan.FromHours(20))
                .Select(s => s.Id)
                .ToList();

            if (staleIds.Count > 0)
            {
                _commandQueueManager.Push(new RefreshSeriesCommand(staleIds), trigger: CommandTrigger.Scheduled);
            }

            var running = _siteDownloadService.GetDownloads()
                .Where(d => d.Status == SiteDownloadStatus.Downloading)
                .ToList();

            var missing = 0;
            var started = 0;

            foreach (var series in aniListSeries)
            {
                var episodes = _episodeService.GetEpisodeBySeries(series.Id);

                // Only new episodes (see SiteNewEpisodes); older gaps are the
                // backlog, downloaded by hand from the Sites page.
                var lastOnDisk = SiteNewEpisodes.LastOnDiskAirDate(episodes);

                var wanted = episodes
                    .Where(e => e.Monitored
                                && !e.HasFile
                                && e.AirDateUtc.HasValue
                                && e.AirDateUtc.Value <= now
                                && SiteNewEpisodes.IsNew(e, lastOnDisk, now))
                    .OrderBy(e => e.SeasonNumber)
                    .ThenBy(e => e.EpisodeNumber)
                    .ToList();

                missing += wanted.Count;

                foreach (var episode in wanted)
                {
                    if (started >= MaxDownloadsPerRun)
                    {
                        break;
                    }

                    if (running.Any(d => d.SeriesId == series.Id && d.EpisodeNumber == episode.EpisodeNumber))
                    {
                        continue;
                    }

                    if (StartDownload(series, episode))
                    {
                        started++;
                    }
                }
            }

            _logger.Info("Sites series sync: {0} aired-but-missing new episode(s) across {1} series, started {2} download(s)", missing, aniListSeries.Count, started);
        }

        // Exactly what the Download button on the Sites page does: resolve the
        // episode's sources on the show's own site and download them, trying
        // each in turn. Tries the show's catalogue rows until one yields a
        // download; null = nothing on the site yet, or we already have it.
        private bool StartDownload(Series series, Episode episode)
        {
            foreach (var rowId in _siteShowService.FindDownloadRowIds(series, episode.SeasonNumber))
            {
                try
                {
                    if (_siteDownloadService.StartDownload(rowId, episode.EpisodeNumber, null, skipIfPresent: true) != null)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Sites series sync: couldn't start '{0}' S{1:00}E{2:00}", series.Title, episode.SeasonNumber, episode.EpisodeNumber);
                }
            }

            return false;
        }
    }
}
