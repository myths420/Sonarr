using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.AnimeSite;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.AnimeSite;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.AnimeSite
{
    // Builds the Series/Episode shape from a catalogue show and its scraped
    // episode list, for shows with no AniList match. SkyHookProxy.GetSeriesInfo
    // delegates here for ids in the SiteSeriesIds band.
    public interface ISiteScrapeSeriesInfoProxy
    {
        Tuple<Series, List<Episode>> GetSeriesInfo(int siteShowId);

        // Local, always-reachable poster URL for the catalogue show behind
        // this AniList id, or null if there's no site poster to serve.
        // Used as a fallback when AniList has no cover image.
        string LocalPosterUrl(int aniListId);

        // Episode list for the catalogue show(s) behind these AniList id(s) --
        // one season per id, earliest first. Empty when none are in the
        // catalogue. Used to keep an AniList-backed series' episode list whole
        // while the AniList API is down, or when it lags the site.
        // allowScrape=false (the common Refresh path) synthesises episodes from
        // the persisted catalogue count with no network call; allowScrape=true
        // (AniList unreachable) does a live episode-list scrape.
        List<Episode> ScrapedEpisodesForAniList(IReadOnlyList<int> aniListIds, bool allowScrape);
    }

    public class SiteScrapeSeriesInfoProxy : ISiteScrapeSeriesInfoProxy
    {
        private readonly ISiteShowRepository _siteShowRepository;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IAnimeSiteCatalogBrowser _catalogBrowser;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly Logger _logger;

        public SiteScrapeSeriesInfoProxy(ISiteShowRepository siteShowRepository,
                                         IIndexerFactory indexerFactory,
                                         IAnimeSiteCatalogBrowser catalogBrowser,
                                         IConfigFileProvider configFileProvider,
                                         Logger logger)
        {
            _siteShowRepository = siteShowRepository;
            _indexerFactory = indexerFactory;
            _catalogBrowser = catalogBrowser;
            _configFileProvider = configFileProvider;
            _logger = logger;
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(int siteShowId)
        {
            var show = _siteShowRepository.Get(siteShowId);
            if (show == null)
            {
                throw new SeriesNotFoundException(SiteSeriesIds.FromSiteShowId(siteShowId));
            }

            var series = MapSeries(show);
            var episodes = MapEpisodes(show);

            return new Tuple<Series, List<Episode>>(series, episodes);
        }

        private Series MapSeries(SiteShow show)
        {
            var rawTitle = string.IsNullOrWhiteSpace(show.Title) ? $"Site show {show.Id}" : show.Title;

            // Drop a trailing "Season 1" so the base show and its season
            // rows share a cleaned title (used for folding).
            var title = SeasonTitleParser.Parse(rawTitle).BaseTitle;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = rawTitle;
            }

            var series = new Series
            {
                TvdbId = SiteSeriesIds.FromSiteShowId(show.Id),
                Title = title,
                CleanTitle = Parser.Parser.CleanSeriesTitle(title),
                SortTitle = SeriesTitleNormalizer.Normalize(title, 0),
                TitleSlug = $"site-{show.Id}",
                Overview = show.Overview,
                Status = MapStatus(show.Status),
                Network = "AnimeSite",
                Runtime = 24,
                SeriesType = SeriesTypes.Anime,
                Year = show.Year,
                Genres = string.IsNullOrWhiteSpace(show.Genres)
                    ? new List<string>()
                    : show.Genres.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(g => g.Trim()).ToList(),
                Seasons = new List<Season> { new Season { SeasonNumber = 1, Monitored = true } },
                Images = new List<MediaCover.MediaCover>(),
                Ratings = new Ratings(),
                Monitored = true
            };

            // Serve the poster from our own cached copy (SiteShowPosterService
            // fetched it with the Referer header the site's hotlink
            // protection needs) rather than the raw site URL, which Sonarr's
            // MediaCoverService gets a 403 on.
            var poster = PosterUrlFor(show);
            if (poster != null)
            {
                series.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Poster, poster));
            }

            if (show.Year > 0)
            {
                series.FirstAired = new DateTime(show.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }

            return series;
        }

        public List<Episode> ScrapedEpisodesForAniList(IReadOnlyList<int> aniListIds, bool allowScrape)
        {
            var episodes = new List<Episode>();
            if (aniListIds == null)
            {
                return episodes;
            }

            for (var i = 0; i < aniListIds.Count; i++)
            {
                var show = _siteShowRepository.FindByAniListId(aniListIds[i]);
                if (show == null)
                {
                    continue;
                }

                var seasonNumber = i + 1;
                var cached = show.GetCachedEpisodes();

                if (cached.Count > 0)
                {
                    episodes.AddRange(EpisodesFromCache(cached, seasonNumber));
                }
                else if (allowScrape)
                {
                    episodes.AddRange(MapEpisodes(show, seasonNumber));
                }
                else
                {
                    episodes.AddRange(SynthesiseEpisodes(show.Episodes, seasonNumber));
                }
            }

            return episodes;
        }

        private static List<Episode> EpisodesFromCache(List<SiteShowEpisode> cached, int seasonNumber)
        {
            return cached
                .Where(e => e.Number > 0)
                .GroupBy(e => e.Number)
                .Select(g => g.First())
                .OrderBy(e => e.Number)
                .Select(e => new Episode
                {
                    SeasonNumber = seasonNumber,
                    EpisodeNumber = e.Number,
                    AbsoluteEpisodeNumber = e.Number,
                    Title = string.IsNullOrWhiteSpace(e.Title) ? $"Episode {e.Number}" : e.Title,
                    AirDate = e.AirDateUtc?.ToString(Episode.AIR_DATE_FORMAT),
                    AirDateUtc = e.AirDateUtc,
                    Monitored = true
                })
                .ToList();
        }

        private static List<Episode> SynthesiseEpisodes(int count, int seasonNumber)
        {
            var episodes = new List<Episode>();
            for (var n = 1; n <= count; n++)
            {
                episodes.Add(new Episode
                {
                    SeasonNumber = seasonNumber,
                    EpisodeNumber = n,
                    AbsoluteEpisodeNumber = n,
                    Title = $"Episode {n}",
                    Monitored = true
                });
            }

            return episodes;
        }

        private List<Episode> MapEpisodes(SiteShow show, int seasonNumber = 1)
        {
            List<AnimeSiteEpisodeEntry> entries;

            try
            {
                var indexerSettings = _indexerFactory.All()
                    .FirstOrDefault(d => d.Id == show.SourceListId && d.Implementation == "AnimeSiteIndexer")?.Settings as AnimeSiteSettings;

                entries = indexerSettings == null
                    ? new List<AnimeSiteEpisodeEntry>()
                    : _catalogBrowser.BrowseEpisodes(AnimeSiteCatalogueOptions.FromIndexer(indexerSettings), show.Url, _logger);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't scrape episode list for '{0}' -- adding series with no episodes", show.Title);
                entries = new List<AnimeSiteEpisodeEntry>();
            }

            return entries
                .Where(e => e.Number > 0)
                .GroupBy(e => e.Number)
                .Select(g => g.First())
                .OrderBy(e => e.Number)
                .Select(e =>
                {
                    var airDate = SiteEpisodeTitle.ParseAirDate(e.Title);

                    return new Episode
                    {
                        SeasonNumber = seasonNumber,
                        EpisodeNumber = e.Number,
                        AbsoluteEpisodeNumber = e.Number,
                        Title = string.IsNullOrWhiteSpace(e.Title) ? $"Episode {e.Number}" : e.Title,
                        AirDate = airDate?.ToString(Episode.AIR_DATE_FORMAT),
                        AirDateUtc = airDate,
                        Monitored = true
                    };
                })
                .ToList();
        }

        public string LocalPosterUrl(int aniListId)
        {
            if (aniListId <= 0)
            {
                return null;
            }

            var show = _siteShowRepository.FindByAniListId(aniListId);
            return show == null ? null : PosterUrlFor(show);
        }

        private string PosterUrlFor(SiteShow show)
        {
            if (string.IsNullOrWhiteSpace(show.PosterUrl))
            {
                return null;
            }

            // Our own API endpoint, absolute so MediaCoverService can fetch
            // it: http://localhost:<port><urlbase>/api/v5/siteshow/<id>/poster
            var port = _configFileProvider.Port;
            var urlBase = _configFileProvider.UrlBase ?? string.Empty;
            var apiKey = _configFileProvider.ApiKey;

            return $"http://localhost:{port}{urlBase}/api/v5/siteshow/{show.Id}/poster?apikey={apiKey}";
        }

        private static SeriesStatusType MapStatus(string status)
        {
            var s = (status ?? string.Empty).ToUpperInvariant();
            if (s.Contains("FINISH") || s.Contains("COMPLETE") || s.Contains(" END"))
            {
                return SeriesStatusType.Ended;
            }

            return SeriesStatusType.Continuing;
        }
    }
}
