using NzbDrone.Core.Indexers.AnimeSite;

namespace NzbDrone.Core.ImportLists.AnimeSite
{
    // Catalogue-browse settings, built from either an AnimeSite indexer or
    // an AnimeSiteImportList.
    public class AnimeSiteCatalogueOptions
    {
        public string BaseUrl { get; set; }
        public int MaxPages { get; set; } = 3;
        public string BrowsePathPattern { get; set; }
        public string SeriesLinkSelector { get; set; }
        public string TitleCleanupRegex { get; set; }
        public string ScrapingScript { get; set; }
        public AnimeSiteFetchOptions Fetch { get; set; } = AnimeSiteFetchOptions.Direct;

        public string GetSeriesLinkSelector()
        {
            return string.IsNullOrWhiteSpace(SeriesLinkSelector) ? ".listupd .bsx > a" : SeriesLinkSelector;
        }

        public static AnimeSiteCatalogueOptions FromIndexer(AnimeSiteSettings settings)
        {
            return new AnimeSiteCatalogueOptions
            {
                BaseUrl = settings.BaseUrl,
                MaxPages = settings.CatalogueMaxPages > 0 ? settings.CatalogueMaxPages : 3,
                ScrapingScript = string.IsNullOrWhiteSpace(settings.CatalogueScript)
                    ? AnimeSiteImportListSettings.DefaultScrapingScript
                    : settings.CatalogueScript,
                Fetch = AnimeSiteFetchOptions.FromSettings(settings)
            };
        }

        public static AnimeSiteCatalogueOptions FromImportList(AnimeSiteImportListSettings settings)
        {
            return new AnimeSiteCatalogueOptions
            {
                BaseUrl = settings.BaseUrl,
                MaxPages = settings.MaxPages,
                BrowsePathPattern = settings.BrowsePathPattern,
                SeriesLinkSelector = settings.SeriesLinkSelector,
                TitleCleanupRegex = settings.TitleCleanupRegex,
                ScrapingScript = settings.ScrapingScript
            };
        }
    }
}
