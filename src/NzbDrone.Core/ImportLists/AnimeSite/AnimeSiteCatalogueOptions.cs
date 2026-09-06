using NzbDrone.Core.Indexers.AnimeSite;

namespace NzbDrone.Core.ImportLists.AnimeSite
{
    // What the catalogue browser actually needs, decoupled from any one
    // provider's settings class. The Sites catalogue is driven by the
    // AnimeSite *indexer* (adding a site there is all it takes); the
    // AnimeSiteImportList provider -- for people who also want native
    // "Add Series" discovery -- can produce one of these too.
    public class AnimeSiteCatalogueOptions
    {
        public string BaseUrl { get; set; }
        public int MaxPages { get; set; } = 3;
        public string BrowsePathPattern { get; set; }
        public string SeriesLinkSelector { get; set; }
        public string TitleCleanupRegex { get; set; }
        public string ScrapingScript { get; set; }

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
                    : settings.CatalogueScript
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
