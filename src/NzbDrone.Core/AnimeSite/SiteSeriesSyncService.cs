using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource.AniList;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Commands;

namespace NzbDrone.Core.AnimeSite
{
    public class SiteSeriesSyncService : IExecute<SiteSeriesSyncCommand>
    {
        // Don't re-search an episode we tried recently -- the release may
        // simply not be posted on the site yet.
        private static readonly TimeSpan SearchCooldown = TimeSpan.FromHours(6);

        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public SiteSeriesSyncService(ISeriesService seriesService,
                                     IEpisodeService episodeService,
                                     IManageCommandQueue commandQueueManager,
                                     Logger logger)
        {
            _seriesService = seriesService;
            _episodeService = episodeService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Execute(SiteSeriesSyncCommand message)
        {
            var aniListSeries = _seriesService.GetAllSeries()
                .Where(s => AniListSeriesIds.IsAniListId(s.TvdbId) && s.Monitored)
                .ToList();

            if (aniListSeries.Count == 0)
            {
                _logger.Debug("No monitored AniList-backed series to sync");
                return;
            }

            var seriesIds = aniListSeries.Select(s => s.Id).ToList();

            // Pull in episodes that have aired since the last run (new
            // AniList airing-schedule entries become Episode rows here).
            _commandQueueManager.Push(new RefreshSeriesCommand(seriesIds), trigger: CommandTrigger.Scheduled);

            var now = DateTime.UtcNow;
            var wanted = new List<int>();

            foreach (var series in aniListSeries)
            {
                var episodes = _episodeService.GetEpisodeBySeries(series.Id);

                wanted.AddRange(episodes
                    .Where(e => e.Monitored
                                && !e.HasFile
                                && e.AirDateUtc.HasValue
                                && e.AirDateUtc.Value <= now
                                && (e.LastSearchTime == null || now - e.LastSearchTime.Value > SearchCooldown))
                    .Select(e => e.Id));
            }

            if (wanted.Count == 0)
            {
                _logger.Debug("AniList series sync: {0} series checked, nothing missing to search", aniListSeries.Count);
                return;
            }

            _logger.Info("AniList series sync: searching for {0} aired-but-missing episode(s) across {1} series", wanted.Count, aniListSeries.Count);
            _commandQueueManager.Push(new EpisodeSearchCommand(wanted), trigger: CommandTrigger.Scheduled);
        }
    }
}
