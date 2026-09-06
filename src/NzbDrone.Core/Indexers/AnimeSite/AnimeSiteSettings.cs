using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp;
using Equ;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    public class AnimeSiteSettingsValidator : AbstractValidator<AnimeSiteSettings>
    {
        public AnimeSiteSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();

            RuleFor(c => c.SearchUrlPattern).NotEmpty()
                .WithMessage("'Search URL Pattern' must not be empty.");
            RuleFor(c => c.SearchUrlPattern).Must(p => p.Contains("{query}"))
                .WithMessage("'Search URL Pattern' must contain a {query} placeholder, e.g. /?s={query}")
                .When(c => !string.IsNullOrWhiteSpace(c.SearchUrlPattern));

            RuleFor(c => c.EpisodeUrlPattern).NotEmpty()
                .WithMessage("'Episode URL Pattern' must not be empty.");

            // Must be a valid regex AND contain exactly one capture group
            // (the episode number) -- otherwise int.Parse(match.Groups[1])
            // in the parser blows up on every search instead of failing
            // once, clearly, here.
            RuleFor(c => c.EpisodeUrlPattern).Must(BeAValidRegexWithOneGroup)
                .WithMessage("'Episode URL Pattern' must be a valid regex with exactly one capture group for the episode number, e.g. -episode-(\\d+)(?:-|/|$)")
                .When(c => !string.IsNullOrWhiteSpace(c.EpisodeUrlPattern));

            RuleFor(c => c.DirectDownloadHosts).NotEmpty()
                .WithMessage("'Direct Download Hosts' must list at least one host.");

            RuleFor(c => c.LinkResolutionRules).Must(BeValidResolutionRulesJson)
                .WithMessage("'Link Resolution Rules' must be a JSON array of objects, each with a 'hostContains' string and either a 'resolveSelector' string or both 'urlReplaceFrom'/'urlReplaceTo' strings.")
                .When(c => !string.IsNullOrWhiteSpace(c.LinkResolutionRules));

            RuleFor(c => c.SeriesLinkSelector).NotEmpty()
                .WithMessage("'Series Link Selector' must not be empty.");
            RuleFor(c => c.SeriesLinkSelector).Must(BeAValidCssSelector)
                .WithMessage("'Series Link Selector' is not a valid CSS selector.")
                .When(c => !string.IsNullOrWhiteSpace(c.SeriesLinkSelector));

            RuleFor(c => c.EpisodeLinkSelector).NotEmpty()
                .WithMessage("'Episode Link Selector' must not be empty.");
            RuleFor(c => c.EpisodeLinkSelector).Must(BeAValidCssSelector)
                .WithMessage("'Episode Link Selector' is not a valid CSS selector.")
                .When(c => !string.IsNullOrWhiteSpace(c.EpisodeLinkSelector));

            RuleFor(c => c.DownloadLinkSelector).NotEmpty()
                .WithMessage("'Download Link Selector' must not be empty.");
            RuleFor(c => c.DownloadLinkSelector).Must(BeAValidCssSelector)
                .WithMessage("'Download Link Selector' is not a valid CSS selector.")
                .When(c => !string.IsNullOrWhiteSpace(c.DownloadLinkSelector));
        }

        private static bool BeAValidRegexWithOneGroup(string pattern)
        {
            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                return regex.GetGroupNumbers().Length == 2; // group 0 (whole match) + 1 capture group
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // AngleSharp throws on QuerySelectorAll(...) with a malformed
        // selector -- validate at save time instead of on the first search,
        // same reasoning as the regex check above. An empty document is
        // enough to exercise the selector's syntax without needing a real
        // page fetch here.
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

        private static bool BeValidResolutionRulesJson(string json)
        {
            try
            {
                var rules = System.Text.Json.JsonSerializer.Deserialize<List<LinkResolutionRule>>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (rules == null)
                {
                    return false;
                }

                foreach (var rule in rules)
                {
                    if (string.IsNullOrWhiteSpace(rule.HostContains))
                    {
                        return false;
                    }

                    var hasSelector = !string.IsNullOrWhiteSpace(rule.ResolveSelector);
                    var hasReplace = !string.IsNullOrWhiteSpace(rule.UrlReplaceFrom) && rule.UrlReplaceTo != null;
                    if (!hasSelector && !hasReplace)
                    {
                        return false;
                    }

                    if (hasSelector && !BeAValidCssSelector(rule.ResolveSelector))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // One "hop" the link resolver can perform on a candidate download URL:
    // if the URL's host matches HostContains, either (a) fetch that page and
    // replace the URL with the href found by ResolveSelector (e.g.
    // Mediafire's landing-page "Download" button), or (b) do a plain string
    // substitution on the URL (e.g. mirrored.to's dl=0 -> dl=1). Rules are
    // tried in order, repeatedly, until none match or a hop limit is hit --
    // this is what makes a new site's landing-page quirks editable from
    // Sonarr's UI instead of needing a C# code change.
    public class LinkResolutionRule
    {
        public string HostContains { get; set; }
        public string ResolveSelector { get; set; }
        public string UrlReplaceFrom { get; set; }
        public string UrlReplaceTo { get; set; }
    }

    // One instance of this indexer = one website. Add another instance
    // (Settings > Indexers > Add) with a different BaseUrl to add another
    // site. Every part of the scrape that plausibly differs between sites
    // is now a field below instead of hardcoded in AnimeSiteParser:
    //   - which links on the search-results page count as series links
    //   - which links on a series page count as episode links, and how the
    //     episode number is pulled out of one
    //   - which links on an episode page count as real download links, and
    //     which hosts those are allowed to point at
    // A site that still follows the general "search page -> series page ->
    // episode page with download-host links" shape should be addable with
    // just these fields, no new parser class. A site with a fundamentally
    // different flow (e.g. requires JS-rendered content, a login, or a
    // completely different navigation shape) would still need real code.
    public class AnimeSiteSettings : PropertywiseEquatable<AnimeSiteSettings>, IIndexerSettings
    {
        private static readonly AnimeSiteSettingsValidator Validator = new();

        public AnimeSiteSettings()
        {
            BaseUrl = "https://animexin.dev";
            SearchUrlPattern = "/?s={query}";
            SeriesLinkSelector = "a[href]";
            EpisodeLinkSelector = "a[href]";
            EpisodeUrlPattern = @"-episode-(\d+)(?:-|/|$)";
            DownloadLinkSelector = "a[href]";
            DirectDownloadHosts = "mediafire.com,mirrored.to,terabox.com,1024terabox.com";
            LinkResolutionRules = "[{\"hostContains\":\"mediafire.com\",\"resolveSelector\":\"a#downloadButton[href], a.input.popsok[href]\"},{\"hostContains\":\"mirrored.to\",\"urlReplaceFrom\":\"dl=0\",\"urlReplaceTo\":\"dl=1\"}]";
            MultiLanguages = Array.Empty<int>();
            FailDownloads = Array.Empty<int>();

            // Left empty on purpose -- an empty Catalogue Script means "use
            // the current built-in default" (AnimeSiteCatalogueOptions.
            // FromIndexer), so improvements to that default are picked up
            // without every saved indexer carrying a stale copy.
        }

        [FieldDefinition(0, Label = "Website URL", HelpText = "The site to search, e.g. https://animexin.dev or https://donghuaworld.com")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Search URL Pattern", Type = FieldType.Textbox, HelpText = "Path (relative to Website URL) used to search this site. {query} is replaced with the URL-encoded series title. Default: /?s={query}")]
        public string SearchUrlPattern { get; set; }

        [FieldDefinition(2, Label = "Series Link Selector", Type = FieldType.Textbox, Advanced = true, HelpText = "CSS selector for candidate series links on the search-results page. Each matched element's text is compared against the series title. Default 'a[href]' checks every link on the page -- narrow this (e.g. '.result-item a') if that's too slow or matches the wrong things.")]
        public string SeriesLinkSelector { get; set; }

        [FieldDefinition(3, Label = "Episode Link Selector", Type = FieldType.Textbox, Advanced = true, HelpText = "CSS selector for candidate episode links on a series page. Narrow this (e.g. '.eplister a') if the default 'a[href]' picks up unrelated links (related-shows widgets, etc.) that happen to match the Episode URL Pattern below.")]
        public string EpisodeLinkSelector { get; set; }

        [FieldDefinition(4, Label = "Episode URL Pattern", Type = FieldType.Textbox, HelpText = "Regex with one capture group matching the absolute episode number in an episode link's URL. Default matches URLs like .../show-episode-42-subbed/")]
        public string EpisodeUrlPattern { get; set; }

        [FieldDefinition(5, Label = "Download Link Selector", Type = FieldType.Textbox, Advanced = true, HelpText = "CSS selector for candidate download links on an episode page. Narrow this (e.g. '.soraurlx a') if the default 'a[href]' picks up unrelated links that happen to point at one of the Direct Download Hosts below.")]
        public string DownloadLinkSelector { get; set; }

        [FieldDefinition(6, Label = "Direct Download Hosts", Type = FieldType.Textbox, HelpText = "Comma-separated list of hostnames that count as a real direct-download link on an episode page (e.g. mediafire.com,mirrored.to). Links to any other host are ignored.")]
        public string DirectDownloadHosts { get; set; }

        [FieldDefinition(7, Label = "Link Resolution Rules", Type = FieldType.Textbox, Advanced = true, HelpText = "JSON array describing how to turn a landing-page link into the real file URL, per host -- this is the part that's genuinely different per site (e.g. Mediafire needs its Download button's href scraped from a second page; mirrored.to just needs dl=0 changed to dl=1 in the URL). Each entry: {\"hostContains\":\"...\", \"resolveSelector\":\"...\"} to fetch the page and pull a link out via CSS selector, or {\"hostContains\":\"...\", \"urlReplaceFrom\":\"...\", \"urlReplaceTo\":\"...\"} for a plain substitution. Add more entries here for a new site instead of writing code. Ignored entirely if Scraping Script (below) is set.")]
        public string LinkResolutionRules { get; set; }

        [FieldDefinition(8, Label = "Scraping Script", Type = FieldType.Textbox, Advanced = true, HelpText = "Optional JavaScript that fully replaces ALL of the fields above -- write your own findSeriesUrl(searchHtml, seriesTitle), findEpisodeUrl(seriesHtml, episodeNumber), and getReleases(episodeHtml, episodeUrl, seriesTitle, episodeNumber) functions. Use host.get(url), host.select(html, cssSelector) (returns a JSON array of {text,href}, use JSON.parse), host.selectOne(html, cssSelector) (JSON object or null), and host.log(msg). getReleases must return JSON.stringify()'d [{title, url}, ...] -- do quality/language filtering and any landing-page hop-following (call host.get again) right in the script. This is what lets a genuinely arbitrary site (not just ones shaped like animexin) be added without a code change. Leave empty to use the simpler fields above instead.")]
        public string ScrapingScript { get; set; }

        [FieldDefinition(9, Type = FieldType.Select, SelectOptions = typeof(RealLanguageFieldConverter), Label = "IndexerSettingsMultiLanguageRelease", HelpText = "IndexerSettingsMultiLanguageReleaseHelpText", Advanced = true)]
        public IEnumerable<int> MultiLanguages { get; set; }

        [FieldDefinition(10, Type = FieldType.Select, SelectOptions = typeof(FailDownloads), Label = "IndexerSettingsFailDownloads", HelpText = "IndexerSettingsFailDownloadsHelpText", Advanced = true)]
        public IEnumerable<int> FailDownloads { get; set; }

        [FieldDefinition(11, Label = "Catalogue Script", Type = FieldType.Textbox, Advanced = true, HelpText = "JavaScript that lists this site's whole catalogue for the Sites section: define listShows(baseUrl, maxPages) returning JSON.stringify()'d [{title, url}, ...] and listEpisodes(showHtml, showUrl) returning [{number, title, url}, ...]. Same host.* helpers as Scraping Script. The default reads the site's Yoast sitemap. Adding a site here is all it takes for it to appear under Sites -- no separate import list needed.")]
        public string CatalogueScript { get; set; }

        [FieldDefinition(12, Label = "Catalogue Max Pages", Type = FieldType.Number, Advanced = true, HelpText = "How many listing pages listShows() is asked to walk each catalogue sync. The default sitemap script ignores this. Default: 3")]
        public int CatalogueMaxPages { get; set; }

        [FieldDefinition(13, Label = "Headless Browser URL", Type = FieldType.Textbox, Advanced = true, HelpText = "A FlareSolverr endpoint (e.g. http://localhost:8191/v1). When set, page fetches for this site can go through a real headless browser so Cloudflare's \"Just a moment\" interstitial and other JS-gated pages can be read. Note: a Turnstile captcha (e.g. vikingfile's download gate) still can't be solved automatically. Leave empty to always fetch directly.")]
        public string HeadlessBrowserUrl { get; set; }

        [FieldDefinition(14, Label = "Headless Browser Mode", Type = FieldType.Select, SelectOptions = typeof(AnimeSiteBrowserMode), Advanced = true, HelpText = "Off: never use it. Auto: fetch directly, fall back to the headless browser only when a page comes back as a Cloudflare challenge (recommended). Always: route every fetch for this site through the headless browser (slower).")]
        public int HeadlessBrowserMode { get; set; }

        [FieldDefinition(15, Label = "Session Clearance Cookie", Type = FieldType.Textbox, Advanced = true, Privacy = PrivacyLevel.Password, HelpText = "Manual session sync -- an alternative to a headless browser for a Cloudflare-gated site. Clear the challenge once in a real browser, then paste its cf_clearance cookie here (the whole 'cf_clearance=...; ...' string is fine -- only cf_clearance is used). Must be paired with the exact User-Agent below. Expires after a while; the health check will tell you when to re-paste.")]
        public string SessionClearanceCookie { get; set; }

        [FieldDefinition(16, Label = "Session User-Agent", Type = FieldType.Textbox, Advanced = true, HelpText = "The exact User-Agent string of the browser you copied the clearance cookie from (about:version / DevTools > navigator.userAgent). Cloudflare ties cf_clearance to the User-Agent, so a mismatch here fails with 403.")]
        public string SessionUserAgent { get; set; }

        // Parsed/derived helpers used by AnimeSiteIndexer when constructing
        // the parser -- kept here so the "how do I turn these text fields
        // into what the parser actually needs" logic lives next to the
        // fields themselves, not duplicated at every call site.
        public string[] GetDirectDownloadHostsArray()
        {
            return (DirectDownloadHosts ?? "")
                .Split(',')
                .Select(h => h.Trim())
                .Where(h => h.Length > 0)
                .ToArray();
        }

        public Regex GetEpisodeUrlRegex()
        {
            return new Regex(
                string.IsNullOrWhiteSpace(EpisodeUrlPattern) ? @"-episode-(\d+)(?:-|/|$)" : EpisodeUrlPattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        public string GetSeriesLinkSelector() => string.IsNullOrWhiteSpace(SeriesLinkSelector) ? "a[href]" : SeriesLinkSelector;

        public List<LinkResolutionRule> GetLinkResolutionRules()
        {
            if (string.IsNullOrWhiteSpace(LinkResolutionRules))
            {
                return new List<LinkResolutionRule>();
            }

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<LinkResolutionRule>>(LinkResolutionRules, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<LinkResolutionRule>();
            }
            catch
            {
                return new List<LinkResolutionRule>();
            }
        }

        public string GetEpisodeLinkSelector() => string.IsNullOrWhiteSpace(EpisodeLinkSelector) ? "a[href]" : EpisodeLinkSelector;
        public string GetDownloadLinkSelector() => string.IsNullOrWhiteSpace(DownloadLinkSelector) ? "a[href]" : DownloadLinkSelector;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
