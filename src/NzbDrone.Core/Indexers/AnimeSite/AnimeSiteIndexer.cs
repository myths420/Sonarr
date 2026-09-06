using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.AnimeSite;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MetadataSource.AniList;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // One instance = one website. Interactive per-episode search only.
    public class AnimeSiteIndexer : HttpIndexerBase<AnimeSiteSettings>
    {
        // The parser only gets the raw IndexerResponse, so the current
        // search's context is stashed here before each Fetch(). Not safe for
        // concurrent Fetch() calls on the same instance.
        private readonly IAnimeSiteReleaseResolver _releaseResolver;
        private readonly IAnimeSiteFetcher _fetcher;
        private readonly Lazy<ISiteShowService> _siteShowService;

        private int _currentAbsoluteEpisodeNumber;
        private string _currentSeriesTitle;

        public AnimeSiteIndexer(IHttpClient httpClient,
                                 IIndexerStatusService indexerStatusService,
                                 IConfigService configService,
                                 IParsingService parsingService,
                                 Logger logger,
                                 ILocalizationService localizationService,
                                 IAnimeSiteReleaseResolver releaseResolver,
                                 IAnimeSiteFetcher fetcher,
                                 Lazy<ISiteShowService> siteShowService)
            : base(httpClient, indexerStatusService, configService, parsingService, logger, localizationService)
        {
            _releaseResolver = releaseResolver;
            _fetcher = fetcher;
            _siteShowService = siteShowService;
        }

        public override string Name => "Anime Site";

        public override DownloadProtocol Protocol => DownloadProtocol.Torrent;

        // These sites have no RSS feed; search only.
        public override bool SupportsRss => false;

        // The inherited Test() does an RSS connection check, which always
        // fails here (no RSS feed) and trips the indexer backoff. Skip it;
        // real problems surface as "0 results" on a search.
        protected override Task Test(List<ValidationFailure> failures)
        {
            return Task.CompletedTask;
        }

        public override bool SupportsSearch => true;

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new AnimeSiteRequestGenerator { Settings = Settings };
        }

        public override IParseIndexerResponse GetParser()
        {
            // Settings are validated on save; GetEpisodeUrlRegex() also has
            // its own default fallback.
            return new AnimeSiteParser(
                _fetcher,
                AnimeSiteFetchOptions.FromSettings(Settings),
                _logger,
                _releaseResolver,
                _currentAbsoluteEpisodeNumber,
                _currentSeriesTitle,
                Settings.GetEpisodeUrlRegex(),
                Settings.GetDirectDownloadHostsArray(),
                Settings.GetSeriesLinkSelector(),
                Settings.GetEpisodeLinkSelector(),
                Settings.GetDownloadLinkSelector(),
                Settings.GetLinkResolutionRules(),
                Settings.ScrapingScript);
        }

        public override Task<IList<ReleaseInfo>> Fetch(AnimeEpisodeSearchCriteria searchCriteria)
        {
            // A synthetic Site/AniList-backed series (added from the Sites
            // catalogue) isn't findable by a site title search -- resolve it
            // straight from the catalogue row instead. This is also what
            // makes SiteSeriesSync's auto-download of new episodes work.
            var tvdbId = searchCriteria.Series?.TvdbId ?? 0;
            if (AniListSeriesIds.IsAniListId(tvdbId) || SiteSeriesIds.IsSiteId(tvdbId))
            {
                return Task.FromResult(FetchSynthetic(searchCriteria));
            }

            _currentAbsoluteEpisodeNumber = searchCriteria.AbsoluteEpisodeNumber;
            _currentSeriesTitle = searchCriteria.Series.Title;
            return base.Fetch(searchCriteria);
        }

        private IList<ReleaseInfo> FetchSynthetic(AnimeEpisodeSearchCriteria searchCriteria)
        {
            var releases = new List<ReleaseInfo>();
            try
            {
                var resolved = _siteShowService.Value.ResolveReleasesForSeries(
                    searchCriteria.Series,
                    searchCriteria.SeasonNumber,
                    searchCriteria.EpisodeNumber);

                foreach (var r in resolved.Where(r => !string.IsNullOrEmpty(r.Url)))
                {
                    releases.Add(new ReleaseInfo
                    {
                        Guid = r.Url,
                        Title = !string.IsNullOrEmpty(r.Title)
                            ? r.Title
                            : $"{searchCriteria.Series.Title} - S{searchCriteria.SeasonNumber:00}E{searchCriteria.EpisodeNumber:00}",
                        DownloadUrl = r.Url,
                        InfoUrl = r.Url,
                        Size = 0,
                        PublishDate = DateTime.UtcNow,
                        DownloadProtocol = DownloadProtocol.Torrent,
                        IndexerId = Definition.Id,
                        Indexer = Definition.Name,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "AnimeSite: failed to resolve releases for synthetic series '{0}'", searchCriteria.Series?.Title);
            }

            return releases;
        }
    }
}
