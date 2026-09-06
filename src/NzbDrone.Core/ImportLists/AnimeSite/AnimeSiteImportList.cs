using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.AnimeSite
{
    // Browses a scraper-driven anime/donghua site (animexin.dev and the like)
    // for shows to surface on Sonarr's native Add Series > Discover screen.
    // The actual "walk the site and list every show" work lives in
    // AnimeSiteCatalogBrowser, shared with the Sites catalogue feature.
    public class AnimeSiteImportList : ImportListBase<AnimeSiteImportListSettings>
    {
        private readonly IAnimeSiteCatalogBrowser _catalogBrowser;

        public AnimeSiteImportList(IAnimeSiteCatalogBrowser catalogBrowser,
                                   IImportListStatusService importListStatusService,
                                   IConfigService configService,
                                   IParsingService parsingService,
                                   ILocalizationService localizationService,
                                   Logger logger)
            : base(importListStatusService, configService, parsingService, localizationService, logger)
        {
            _catalogBrowser = catalogBrowser;
        }

        public override string Name => "Anime Site";

        public override ImportListType ListType => ImportListType.Advanced;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        public override ImportListFetchResult Fetch()
        {
            var series = new List<ImportListItemInfo>();
            var anyFailure = false;

            try
            {
                var shows = _catalogBrowser.Browse(AnimeSiteCatalogueOptions.FromImportList(Settings), _logger);

                var cleanup = string.IsNullOrWhiteSpace(Settings.TitleCleanupRegex)
                    ? null
                    : new Regex(Settings.TitleCleanupRegex, RegexOptions.IgnoreCase);

                foreach (var show in shows)
                {
                    var title = show.Title?.Trim();
                    if (cleanup != null && !string.IsNullOrEmpty(title))
                    {
                        title = cleanup.Replace(title, string.Empty).Trim();
                    }

                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    series.Add(new ImportListItemInfo
                    {
                        Title = title,
                        Year = show.Year,
                        TvdbId = show.TvdbId,
                        MalId = show.MalId,
                        ImdbId = show.ImdbId
                    });
                }

                _logger.Debug("AnimeSite import list found {0} show(s) on {1}", series.Count, Settings.BaseUrl);
                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                anyFailure = true;
                _logger.Warn(ex, "Failed to browse {0} for import list {1}", Settings.BaseUrl, Definition.Name);
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return new ImportListFetchResult(CleanupListItems(series), anyFailure);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            try
            {
                var result = Fetch();

                if (result.Series.Count == 0)
                {
                    failures.Add(new NzbDroneValidationFailure(string.Empty,
                        "No shows were found. Check the Browse Path Pattern, Series Link Selector, or Scraping Script, then see the log for details.")
                    {
                        IsWarning = true
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to browse {0}", Settings.BaseUrl);
                failures.Add(new ValidationFailure(string.Empty, $"Unable to browse site: {ex.Message}"));
            }
        }
    }
}
