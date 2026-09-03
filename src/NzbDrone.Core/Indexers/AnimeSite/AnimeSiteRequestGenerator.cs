using System.Collections.Generic;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    public class AnimeSiteRequestGenerator : IIndexerRequestGenerator
    {
        public AnimeSiteSettings Settings { get; set; }

        public IndexerPageableRequestChain GetRecentRequests()
        {
            // No "recent releases" feed on these sites -- everything is
            // search-driven (matches how tracker.py/main.py's search()
            // works: you search a title, you don't browse a firehose).
            return new IndexerPageableRequestChain();
        }

        public IndexerPageableRequestChain GetSearchRequests(AnimeEpisodeSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            var searchUrl = $"{Settings.BaseUrl.TrimEnd('/')}/?s={System.Uri.EscapeDataString(searchCriteria.Series.Title)}";
            chain.Add(new[] { new IndexerRequest(searchUrl, HttpAccept.Html) });
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(AnimeSeasonSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            var searchUrl = $"{Settings.BaseUrl.TrimEnd('/')}/?s={System.Uri.EscapeDataString(searchCriteria.Series.Title)}";
            chain.Add(new[] { new IndexerRequest(searchUrl, HttpAccept.Html) });
            return chain;
        }

        // These sites are anime/donghua-only -- standard TV search types
        // don't apply, so these all return empty chains rather than issuing
        // requests that could never produce a useful result.
        public IndexerPageableRequestChain GetSearchRequests(SingleEpisodeSearchCriteria searchCriteria) => new();
        public IndexerPageableRequestChain GetSearchRequests(SeasonSearchCriteria searchCriteria) => new();
        public IndexerPageableRequestChain GetSearchRequests(DailyEpisodeSearchCriteria searchCriteria) => new();
        public IndexerPageableRequestChain GetSearchRequests(DailySeasonSearchCriteria searchCriteria) => new();
        public IndexerPageableRequestChain GetSearchRequests(SpecialEpisodeSearchCriteria searchCriteria) => new();
    }
}
