using System.Text.RegularExpressions;
using AngleSharp;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.AnimeSite
{
    public class AnimeSiteImportListSettingsValidator : AbstractValidator<AnimeSiteImportListSettings>
    {
        public AnimeSiteImportListSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();

            RuleFor(c => c.MaxPages).GreaterThan(0).LessThanOrEqualTo(100);

            // Selector path only; a Scraping Script does its own paging.
            RuleFor(c => c.BrowsePathPattern).NotEmpty()
                .WithMessage("'Browse Path Pattern' must not be empty.")
                .When(c => string.IsNullOrWhiteSpace(c.ScrapingScript));
            RuleFor(c => c.BrowsePathPattern).Must(p => p.Contains("{page}"))
                .WithMessage("'Browse Path Pattern' must contain a {page} placeholder, e.g. /anime/?page={page}")
                .When(c => string.IsNullOrWhiteSpace(c.ScrapingScript) && !string.IsNullOrWhiteSpace(c.BrowsePathPattern));

            RuleFor(c => c.SeriesLinkSelector).NotEmpty()
                .WithMessage("'Series Link Selector' must not be empty.")
                .When(c => string.IsNullOrWhiteSpace(c.ScrapingScript));
            RuleFor(c => c.SeriesLinkSelector).Must(BeAValidCssSelector)
                .WithMessage("'Series Link Selector' is not a valid CSS selector.")
                .When(c => string.IsNullOrWhiteSpace(c.ScrapingScript) && !string.IsNullOrWhiteSpace(c.SeriesLinkSelector));

            RuleFor(c => c.TitleCleanupRegex).Must(BeAValidRegex)
                .WithMessage("'Title Cleanup Regex' is not a valid regular expression.")
                .When(c => !string.IsNullOrWhiteSpace(c.TitleCleanupRegex));
        }

        private static bool BeAValidCssSelector(string selector)
        {
            try
            {
                var context = BrowsingContext.New(AngleSharp.Configuration.Default);
                var doc = context.OpenAsync(req => req.Content("<html></html>")).GetAwaiter().GetResult();
                doc.QuerySelectorAll(selector);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool BeAValidRegex(string pattern)
        {
            try
            {
                _ = new Regex(pattern);
                return true;
            }
            catch (System.ArgumentException)
            {
                return false;
            }
        }
    }

    // One instance = one website to browse for the native Add Series screen.
    // With a Scraping Script, that JavaScript pages the site; otherwise the
    // BrowsePathPattern + SeriesLinkSelector fields are used.
    public class AnimeSiteImportListSettings : ImportListSettingsBase<AnimeSiteImportListSettings>
    {
        // Default: read the Yoast /anime-sitemap.xml and derive each show's
        // name from its URL slug. Falls back to the <a href> links when a
        // headless browser returns the sitemap rendered as HTML.
        public const string DefaultScrapingScript = @"function listShows(baseUrl, maxPages) {
  var xml = host.get(baseUrl + '/anime-sitemap.xml');
  var out = [];
  if (!xml) { host.log('anime-sitemap.xml returned nothing'); return JSON.stringify(out); }
  var skip = { '': 1, 'anime': 1, 'anime-sitemap': 1, 'category': 1, 'genre': 1, 'genres': 1, 'season': 1, 'page': 1, 'tag': 1, 'network': 1, 'type': 1, 'studio': 1, 'schedule': 1, 'ongoing': 1, 'completed': 1 };
  var seen = {};

  var urls = [];
  var re = /<loc>\s*([^<]+?)\s*<\/loc>/g, m;
  while ((m = re.exec(xml)) !== null) { urls.push(m[1]); }

  // Headless browsers return the sitemap rendered as an HTML table.
  if (urls.length === 0) {
    var links = JSON.parse(host.select(xml, 'a[href]'));
    for (var j = 0; j < links.length; j++) {
      if (/^https?:\/\//.test(links[j].href)) { urls.push(links[j].href); }
    }
  }

  for (var i = 0; i < urls.length; i++) {
    var url = urls[i];
    // Last path segment = slug; handles /{slug}/ and /anime/{slug}/.
    var pm = url.match(/^https?:\/\/[^\/]+\/(.+?)\/?$/);
    if (!pm) { continue; }
    var segs = pm[1].split('/');
    var slug = segs[segs.length - 1];
    if (skip[slug.toLowerCase()] || slug.indexOf('-episode-') !== -1) { continue; }
    if (seen[url]) { continue; }
    seen[url] = 1;
    var title = slug.replace(/-/g, ' ')
                    .replace(/\s+/g, ' ')
                    .replace(/\b\w/g, function (c) { return c.toUpperCase(); })
                    .trim();
    out.push({ title: title, url: url });
  }
  host.log('anime-sitemap.xml yielded ' + out.length + ' shows');
  return JSON.stringify(out);
}

// Episode list for the show detail view. '.eplister a' is the common
// WordPress anime-theme selector; falls back to any '-episode-<N>-' link.
function listEpisodes(showHtml, showUrl) {
  var byNumber = {};
  var links = JSON.parse(host.select(showHtml, '.eplister a'));
  if (links.length === 0) {
    links = JSON.parse(host.select(showHtml, 'a[href*=""-episode-""]'));
  }

  for (var i = 0; i < links.length; i++) {
    var link = links[i];
    var label = (link.title || link.text || '').replace(/\s+/g, ' ').trim();
    var numberMatch = link.href.match(/-episode-(\d+)/) || label.match(/(\d+)/);
    if (!numberMatch) { continue; }

    var number = parseInt(numberMatch[1], 10);
    var isSpecial = link.href.indexOf('-special') !== -1;
    var existing = byNumber[number];

    // Prefer the plain entry over a -special- variant with the same number.
    if (existing && !(existing.isSpecial && !isSpecial)) { continue; }

    byNumber[number] = {
      number: number,
      title: label || ('Episode ' + number),
      url: link.href,
      isSpecial: isSpecial
    };
  }

  var out = [];
  for (var key in byNumber) {
    out.push({ number: byNumber[key].number, title: byNumber[key].title, url: byNumber[key].url });
  }

  out.sort(function (a, b) { return a.number - b.number; });
  host.log('listEpisodes found ' + out.length + ' episodes for ' + showUrl);
  return JSON.stringify(out);
}";

        private static readonly AnimeSiteImportListSettingsValidator Validator = new();

        public AnimeSiteImportListSettings()
        {
            BaseUrl = "https://animexin.dev";
            BrowsePathPattern = "/anime/?page={page}";
            MaxPages = 3;
            SeriesLinkSelector = ".listupd .bsx > a";
            TitleCleanupRegex = @"\s*(Subtitle Indonesia|Sub Indo|Episode\s*\d+.*)$";
            ScrapingScript = DefaultScrapingScript;
        }

        [FieldDefinition(0, Label = "Website URL", HelpText = "The site to browse, e.g. https://animexin.dev")]
        public override string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Browse Path Pattern", Type = FieldType.Textbox, HelpText = "Path (relative to Website URL) of the show-listing page. {page} is replaced with the page number. Default: /anime/?page={page}. Ignored when a Scraping Script is set.")]
        public string BrowsePathPattern { get; set; }

        [FieldDefinition(2, Label = "Max Pages", Type = FieldType.Number, HelpText = "How many listing pages to walk each sync. Paging stops early on the first page that yields no new shows.")]
        public int MaxPages { get; set; }

        [FieldDefinition(3, Label = "Series Link Selector", Type = FieldType.Textbox, Advanced = true, HelpText = "CSS selector for the show links on a listing page. The link's title attribute is used as the show name, falling back to its text. Ignored when a Scraping Script is set.")]
        public string SeriesLinkSelector { get; set; }

        [FieldDefinition(4, Label = "Title Cleanup Regex", Type = FieldType.Textbox, Advanced = true, HelpText = "Regex whose matches are stripped from each scraped title before it is handed to Sonarr (e.g. a trailing 'Subtitle Indonesia'). Leave empty to keep titles verbatim.")]
        public string TitleCleanupRegex { get; set; }

        [FieldDefinition(5, Label = "Scraping Script", Type = FieldType.Textbox, Advanced = true, HelpText = "Optional JavaScript that fully replaces the fields above -- define listShows(baseUrl, maxPages) returning JSON.stringify()'d [{title, year, tvdbId, malId, imdbId}, ...]. Use host.get(url), host.select(html, cssSelector) (JSON array of {text, href, title}), host.selectOne(...), host.log(msg). Do your own paging with a loop over host.get(). Leave empty to use the simpler fields above.")]
        public string ScrapingScript { get; set; }

        public string GetSeriesLinkSelector() => string.IsNullOrWhiteSpace(SeriesLinkSelector) ? ".listupd .bsx > a" : SeriesLinkSelector;

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
