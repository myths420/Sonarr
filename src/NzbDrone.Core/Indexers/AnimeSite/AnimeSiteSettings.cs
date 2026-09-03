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
            SeriesLinkSelector = "a[href]";
            EpisodeLinkSelector = "a[href]";
            EpisodeUrlPattern = @"-episode-(\d+)(?:-|/|$)";
            DownloadLinkSelector = "a[href]";
            DirectDownloadHosts = "mediafire.com,mirrored.to,terabox.com,1024terabox.com";
            MultiLanguages = Array.Empty<int>();
            FailDownloads = Array.Empty<int>();
        }

        [FieldDefinition(0, Label = "Website URL", HelpText = "The site to search, e.g. https://animexin.dev or https://donghuaworld.com")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Series Link Selector", Type = FieldType.Textbox, Advanced = true, HelpText = "CSS selector for candidate series links on the search-results page. Each matched element's text is compared against the series title. Default 'a[href]' checks every link on the page -- narrow this (e.g. '.result-item a') if that's too slow or matches the wrong things.")]
        public string SeriesLinkSelector { get; set; }

        [FieldDefinition(2, Label = "Episode Link Selector", Type = FieldType.Textbox, Advanced = true, HelpText = "CSS selector for candidate episode links on a series page. Narrow this (e.g. '.eplister a') if the default 'a[href]' picks up unrelated links (related-shows widgets, etc.) that happen to match the Episode URL Pattern below.")]
        public string EpisodeLinkSelector { get; set; }

        [FieldDefinition(3, Label = "Episode URL Pattern", Type = FieldType.Textbox, HelpText = "Regex with one capture group matching the absolute episode number in an episode link's URL. Default matches URLs like .../show-episode-42-subbed/")]
        public string EpisodeUrlPattern { get; set; }

        [FieldDefinition(4, Label = "Download Link Selector", Type = FieldType.Textbox, Advanced = true, HelpText = "CSS selector for candidate download links on an episode page. Narrow this (e.g. '.soraurlx a') if the default 'a[href]' picks up unrelated links that happen to point at one of the Direct Download Hosts below.")]
        public string DownloadLinkSelector { get; set; }

        [FieldDefinition(5, Label = "Direct Download Hosts", Type = FieldType.Textbox, HelpText = "Comma-separated list of hostnames that count as a real direct-download link on an episode page (e.g. mediafire.com,mirrored.to). Links to any other host are ignored.")]
        public string DirectDownloadHosts { get; set; }

        [FieldDefinition(6, Type = FieldType.Select, SelectOptions = typeof(RealLanguageFieldConverter), Label = "IndexerSettingsMultiLanguageRelease", HelpText = "IndexerSettingsMultiLanguageReleaseHelpText", Advanced = true)]
        public IEnumerable<int> MultiLanguages { get; set; }

        [FieldDefinition(7, Type = FieldType.Select, SelectOptions = typeof(FailDownloads), Label = "IndexerSettingsFailDownloads", HelpText = "IndexerSettingsFailDownloadsHelpText", Advanced = true)]
        public IEnumerable<int> FailDownloads { get; set; }

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
        public string GetEpisodeLinkSelector() => string.IsNullOrWhiteSpace(EpisodeLinkSelector) ? "a[href]" : EpisodeLinkSelector;
        public string GetDownloadLinkSelector() => string.IsNullOrWhiteSpace(DownloadLinkSelector) ? "a[href]" : DownloadLinkSelector;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
