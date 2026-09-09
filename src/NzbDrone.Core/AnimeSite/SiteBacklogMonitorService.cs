using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource.AniList;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.AnimeSite
{
    // A Refresh rebuilds a synthetic series' episode list from the catalogue,
    // and RefreshEpisodeService monitors every new episode whose season is
    // monitored. For a series that just gained its whole back catalogue that
    // is hundreds of long-aired episodes the scheduled search would grab all
    // at once. Keep monitoring only what is recent, upcoming, or already on
    // disk; leave the old gaps for a manual "Download All".
    public class SiteBacklogMonitorService : IHandle<EpisodeInfoRefreshedEvent>
    {
        private const int BacklogMonitorDays = 35;

        private readonly IEpisodeService _episodeService;
        private readonly Logger _logger;

        public SiteBacklogMonitorService(IEpisodeService episodeService, Logger logger)
        {
            _episodeService = episodeService;
            _logger = logger;
        }

        public void Handle(EpisodeInfoRefreshedEvent message)
        {
            var series = message.Series;
            if (series == null || !(AniListSeriesIds.IsAniListId(series.TvdbId) || SiteSeriesIds.IsSiteId(series.TvdbId)))
            {
                return;
            }

            if (message.Added.Count == 0 && message.Updated.Count == 0)
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-BacklogMonitorDays);

            var stale = message.Added.Concat(message.Updated)
                .Where(e => e.Monitored
                            && !e.HasFile
                            && e.AirDateUtc.HasValue
                            && e.AirDateUtc.Value < cutoff)
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .ToList();

            if (stale.Count == 0)
            {
                return;
            }

            foreach (var episode in stale)
            {
                episode.Monitored = false;
            }

            _episodeService.UpdateEpisodes(stale);
            _logger.Info("Sites: unmonitored {0} long-aired missing episode(s) on '{1}' so they aren't auto-searched", stale.Count, series.Title);
        }
    }
}
