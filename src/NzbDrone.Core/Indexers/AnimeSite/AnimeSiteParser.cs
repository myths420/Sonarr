using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Jint;
using NLog;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // Parses an interactive search: run the Scraping Script if set,
    // otherwise use the SeriesLinkSelector / EpisodeLinkSelector fields.
    // Release extraction is delegated to AnimeSiteReleaseResolver.
    public class AnimeSiteParser : IParseIndexerResponse
    {
        private readonly IAnimeSiteFetcher _fetcher;
        private readonly AnimeSiteFetchOptions _fetch;
        private readonly Logger _logger;
        private readonly IAnimeSiteReleaseResolver _releaseResolver;
        private readonly Regex _episodeUrlRegex;
        private readonly string _seriesLinkSelector;
        private readonly string _episodeLinkSelector;
        private readonly AnimeSiteReleaseOptions _releaseOptions;
        private readonly string _scrapingScript;

        public AnimeSiteParser(IAnimeSiteFetcher fetcher, AnimeSiteFetchOptions fetch, Logger logger, IAnimeSiteReleaseResolver releaseResolver, int absoluteEpisodeNumber, string seriesTitle, Regex episodeUrlRegex, string[] directDlHosts, string seriesLinkSelector, string episodeLinkSelector, string downloadLinkSelector, List<LinkResolutionRule> resolutionRules, string scrapingScript)
        {
            _fetcher = fetcher;
            _fetch = fetch ?? AnimeSiteFetchOptions.Direct;
            _logger = logger;
            _releaseResolver = releaseResolver;
            AbsoluteEpisodeNumber = absoluteEpisodeNumber;
            SeriesTitle = seriesTitle;
            _episodeUrlRegex = episodeUrlRegex;
            _seriesLinkSelector = seriesLinkSelector;
            _episodeLinkSelector = episodeLinkSelector;
            _releaseOptions = new AnimeSiteReleaseOptions
            {
                DirectDownloadHosts = directDlHosts,
                DownloadLinkSelector = downloadLinkSelector,
                ResolutionRules = resolutionRules ?? new List<LinkResolutionRule>(),
                ScrapingScript = scrapingScript,
                Fetch = fetch ?? AnimeSiteFetchOptions.Direct
            };
            _scrapingScript = scrapingScript;
        }

        // Set by AnimeSiteIndexer before each Fetch() call.
        public int AbsoluteEpisodeNumber { get; set; }

        public string SeriesTitle { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            if (!string.IsNullOrWhiteSpace(_scrapingScript))
            {
                return ParseResponseViaScript(indexerResponse);
            }

            var releases = new List<ReleaseInfo>();

            try
            {
                var searchDoc = ParseHtml(indexerResponse.Content);
                var animeLink = FindMatchingSeriesLink(searchDoc, SeriesTitle);
                if (string.IsNullOrEmpty(animeLink))
                {
                    _logger.Debug("No search result on this site matched series title '{0}'", SeriesTitle);
                    return releases;
                }

                var episodeLink = FindEpisodeLink(animeLink, AbsoluteEpisodeNumber);
                if (string.IsNullOrEmpty(episodeLink))
                {
                    _logger.Debug("Found series page {0} but no link for absolute episode {1}", animeLink, AbsoluteEpisodeNumber);
                    return releases;
                }

                var episodeHtml = _fetcher.GetHtml(episodeLink, animeLink, _fetch);
                releases.AddRange(ToReleaseInfo(_releaseResolver.GetReleases(_releaseOptions, episodeHtml, episodeLink, SeriesTitle, AbsoluteEpisodeNumber, _logger), episodeLink));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to parse AnimeSite response for {0}", indexerResponse.Request.Url);
            }

            return releases;
        }

        // Script contract:
        //   findSeriesUrl(searchHtml, seriesTitle) -> url or ""
        //   findEpisodeUrl(seriesHtml, episodeNumber) -> url or ""
        //   getReleases(...) -> JSON array of {title, url}
        private IList<ReleaseInfo> ParseResponseViaScript(IndexerResponse indexerResponse)
        {
            var releases = new List<ReleaseInfo>();
            var host = new AnimeSiteScriptHost(_fetcher, _fetch, _logger);

            try
            {
                // Generous: findSeriesUrl/findEpisodeUrl walk season pages
                // and getReleases follows landing-page hops, each of which
                // can be a slow headless-browser fetch.
                var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromMinutes(5)));
                engine.SetValue("host", host);
                engine.Execute(_scrapingScript);

                var seriesUrl = engine.Invoke("findSeriesUrl", indexerResponse.Content, SeriesTitle).AsString();
                if (string.IsNullOrEmpty(seriesUrl))
                {
                    _logger.Debug("Scraping script found no series match for '{0}'", SeriesTitle);
                    return releases;
                }

                var seriesHtml = host.Get(seriesUrl);
                var episodeUrl = engine.Invoke("findEpisodeUrl", seriesHtml, AbsoluteEpisodeNumber).AsString();
                if (string.IsNullOrEmpty(episodeUrl))
                {
                    _logger.Debug("Scraping script found no episode link for #{0}", AbsoluteEpisodeNumber);
                    return releases;
                }

                var episodeHtml = host.Get(episodeUrl);
                releases.AddRange(ToReleaseInfo(_releaseResolver.GetReleases(_releaseOptions, episodeHtml, episodeUrl, SeriesTitle, AbsoluteEpisodeNumber, _logger), episodeUrl));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Scraping script failed for {0}", SeriesTitle);
            }

            return releases;
        }

        private static IEnumerable<ReleaseInfo> ToReleaseInfo(IEnumerable<ResolvedRelease> resolved, string episodeUrl)
        {
            foreach (var release in resolved)
            {
                yield return new ReleaseInfo
                {
                    Guid = release.Url,
                    Title = release.Title,
                    DownloadUrl = release.Url,
                    InfoUrl = episodeUrl,
                    Size = 0,
                    PublishDate = DateTime.UtcNow,
                    DownloadProtocol = Indexers.DownloadProtocol.Torrent,
                };
            }
        }

        // Match a SeriesLinkSelector element by normalized (lowercase,
        // alphanumerics-only) title equality.
        private string FindMatchingSeriesLink(IDocument doc, string title)
        {
            var target = StripTitle(title);

            foreach (var a in doc.QuerySelectorAll(_seriesLinkSelector))
            {
                var href = a.GetAttribute("href");
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                var linkText = a.TextContent?.Trim();
                if (string.IsNullOrEmpty(linkText))
                {
                    continue;
                }

                if (StripTitle(linkText) == target)
                {
                    return href;
                }
            }

            return null;
        }

        private static string StripTitle(string title)
        {
            var cleaned = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]", "");
            return cleaned;
        }

        // Finds the episode link matching EpisodeUrlPattern among the
        // EpisodeLinkSelector elements.
        private string FindEpisodeLink(string animeLink, int absoluteEpisodeNumber)
        {
            var doc = ParseHtml(_fetcher.GetHtml(animeLink, null, _fetch));

            foreach (var a in doc.QuerySelectorAll(_episodeLinkSelector))
            {
                var href = a.GetAttribute("href");
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                var match = _episodeUrlRegex.Match(href);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var episodeNumber) && episodeNumber == absoluteEpisodeNumber)
                {
                    return href;
                }
            }

            return null;
        }

        private static IDocument ParseHtml(string html)
        {
            var config = AngleSharp.Configuration.Default;
            var context = BrowsingContext.New(config);
            return context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();
        }
    }
}
