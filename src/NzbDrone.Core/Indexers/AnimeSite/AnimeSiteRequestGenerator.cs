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

        // Builds the search URL from Settings.SearchUrlPattern instead of a
        // hardcoded "/?s=" scheme -- a site using a different search path
        // (or query param name) just needs a different pattern here, not a
        // code change.
        private string BuildSearchUrl(string title)
        {
            var query = System.Uri.EscapeDataString(title);
            var pattern = string.IsNullOrWhiteSpace(Settings.SearchUrlPattern) ? "/?s={query}" : Settings.SearchUrlPattern;
            var path = pattern.Replace("{query}", query);
            return path.StartsWith("http") ? path : $"{Settings.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
        }

        public IndexerPageableRequestChain GetSearchRequests(AnimeEpisodeSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            chain.Add(new[] { new IndexerRequest(BuildSearchUrl(searchCriteria.Series.Title), HttpAccept.Html) });
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(AnimeSeasonSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            chain.Add(new[] { new IndexerRequest(BuildSearchUrl(searchCriteria.Series.Title), HttpAccept.Html) });
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
