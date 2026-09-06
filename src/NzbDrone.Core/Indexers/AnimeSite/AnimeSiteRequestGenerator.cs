using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    public class AnimeSiteRequestGenerator : IIndexerRequestGenerator
    {
        public AnimeSiteSettings Settings { get; set; }

        public IndexerPageableRequestChain GetRecentRequests()
        {
            // No RSS feed on these sites.
            return new IndexerPageableRequestChain();
        }

        // Search URL from Settings.SearchUrlPattern.
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

        // Anime-only: no standard TV search types.
        public IndexerPageableRequestChain GetSearchRequests(SingleEpisodeSearchCriteria searchCriteria) => new();
        public IndexerPageableRequestChain GetSearchRequests(SeasonSearchCriteria searchCriteria) => new();
        public IndexerPageableRequestChain GetSearchRequests(DailyEpisodeSearchCriteria searchCriteria) => new();
        public IndexerPageableRequestChain GetSearchRequests(DailySeasonSearchCriteria searchCriteria) => new();
        public IndexerPageableRequestChain GetSearchRequests(SpecialEpisodeSearchCriteria searchCriteria) => new();
    }
}
