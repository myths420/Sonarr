using System.Collections.Generic;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // One instance = one website (see AnimeSiteSettings.BaseUrl). Only
    // surfaces results for interactive/manual per-episode search on
    // Anime-type series -- that's the existing "search" magnifying-glass
    // button on a season/episode row in Sonarr's UI; no new UI needed.
    public class AnimeSiteIndexer : HttpIndexerBase<AnimeSiteSettings>
    {
        // HttpIndexerBase.FetchReleases calls GetRequestGenerator() and
        // GetParser() fresh per Fetch(...) call, and GetParser()'s result
        // (IParseIndexerResponse.ParseResponse) only ever receives the raw
        // IndexerResponse -- not the original AnimeEpisodeSearchCriteria.
        // Stashing the fields the parser needs here, set immediately before
        // calling into the base implementation, is how that context gets
        // through. This means it is NOT safe to have multiple concurrent
        // Fetch() calls in flight on the same indexer instance -- fine for
        // this indexer's actual usage (one interactive search at a time),
        // but worth knowing if this is ever repurposed for something that
        // fires many searches in parallel.
        private int _currentAbsoluteEpisodeNumber;
        private string _currentSeriesTitle;

        public AnimeSiteIndexer(IHttpClient httpClient,
                                 IIndexerStatusService indexerStatusService,
                                 IConfigService configService,
                                 IParsingService parsingService,
                                 Logger logger,
                                 ILocalizationService localizationService)
            : base(httpClient, indexerStatusService, configService, parsingService, logger, localizationService)
        {
        }

        public override string Name => "Anime Site";

        public override DownloadProtocol Protocol => DownloadProtocol.Unknown;

        // No RSS/recent-releases feed on these sites -- everything is
        // search-driven.
        public override bool SupportsRss => false;

        public override bool SupportsSearch => true;

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new AnimeSiteRequestGenerator { Settings = Settings };
        }

        public override IParseIndexerResponse GetParser()
        {
            // Regex/host list are validated in AnimeSiteSettingsValidator
            // when the instance is saved, so this should never throw on a
            // saved instance -- GetEpisodeUrlRegex() still has an internal
            // fallback to the default pattern as a last-resort safety net.
            return new AnimeSiteParser(
                _httpClient,
                _logger,
                _currentAbsoluteEpisodeNumber,
                _currentSeriesTitle,
                Settings.GetEpisodeUrlRegex(),
                Settings.GetDirectDownloadHostsArray(),
                Settings.GetSeriesLinkSelector(),
                Settings.GetEpisodeLinkSelector(),
                Settings.GetDownloadLinkSelector());
        }

        public override Task<IList<ReleaseInfo>> Fetch(AnimeEpisodeSearchCriteria searchCriteria)
        {
            _currentAbsoluteEpisodeNumber = searchCriteria.AbsoluteEpisodeNumber;
            _currentSeriesTitle = searchCriteria.Series.Title;
            return base.Fetch(searchCriteria);
        }
    }
}
