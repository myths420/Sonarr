using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Jint;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // Two ways to scrape a site, in priority order:
    //   1. If AnimeSiteSettings.ScrapingScript is set, run that JavaScript
    //      instead -- it fully controls series matching, episode matching,
    //      and release extraction (quality/language filtering, landing-page
    //      hops, anything) via the `host` bridge object (AnimeSiteScriptHost).
    //      This is what makes a genuinely arbitrary site's logic editable
    //      from Sonarr's settings UI, not just ones shaped like animexin.
    //   2. Otherwise, fall back to the simpler selector-based fields
    //      (SeriesLinkSelector/EpisodeLinkSelector/etc) below -- good enough
    //      for a quick "same general shape as animexin" site without
    //      writing a script at all.
    // Release extraction itself (getReleases()/DownloadLinkSelector +
    // LinkResolutionRules) lives in AnimeSiteReleaseResolver -- shared with
    // the Sites catalogue's download panel, which needs the exact same
    // "episode page -> real download link" logic but has no Series/Episode
    // to search for.
    public class AnimeSiteParser : IParseIndexerResponse
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly IAnimeSiteReleaseResolver _releaseResolver;
        private readonly Regex _episodeUrlRegex;
        private readonly string _seriesLinkSelector;
        private readonly string _episodeLinkSelector;
        private readonly AnimeSiteReleaseOptions _releaseOptions;
        private readonly string _scrapingScript;

        public AnimeSiteParser(IHttpClient httpClient, Logger logger, IAnimeSiteReleaseResolver releaseResolver, int absoluteEpisodeNumber, string seriesTitle, Regex episodeUrlRegex, string[] directDlHosts, string seriesLinkSelector, string episodeLinkSelector, string downloadLinkSelector, List<LinkResolutionRule> resolutionRules, string scrapingScript)
        {
            _httpClient = httpClient;
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
                ScrapingScript = scrapingScript
            };
            _scrapingScript = scrapingScript;
        }

        // Set by AnimeSiteIndexer right before each Fetch() call -- see the
        // comment there for why this can't just come through ParseResponse's
        // IndexerResponse parameter.
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

                var episodeHtml = _httpClient.Get(new HttpRequest(episodeLink)).Content;
                releases.AddRange(ToReleaseInfo(_releaseResolver.GetReleases(_releaseOptions, episodeHtml, episodeLink, SeriesTitle, AbsoluteEpisodeNumber, _logger), episodeLink));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to parse AnimeSite response for {0}", indexerResponse.Request.Url);
            }

            return releases;
        }

        // Runs the configured JavaScript instead of the fixed selector
        // pipeline. Script contract:
        //   findSeriesUrl(searchHtml, seriesTitle) -> url string or ""
        //   findEpisodeUrl(seriesHtml, episodeNumber) -> url string or ""
        //   getReleases(episodeHtml, episodeUrl, seriesTitle, episodeNumber)
        //       -> JSON.stringify()'d array of {title, url} -- see
        //       AnimeSiteReleaseResolver for where this actually runs.
        // Everything crossing the boundary is a plain string (host.get/
        // select/selectOne all return strings, selects as JSON) -- see
        // AnimeSiteScriptHost for why.
        private IList<ReleaseInfo> ParseResponseViaScript(IndexerResponse indexerResponse)
        {
            var releases = new List<ReleaseInfo>();
            var host = new AnimeSiteScriptHost(_httpClient, _logger);

            try
            {
                var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(30)));
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

        // Port of tracker.py's fetch_anime_obj: strip non-alphanumerics,
        // lowercase, exact-compare. Real fuzzy matching (Levenshtein etc.)
        // is deliberately not used here -- same reasoning as the Python
        // version, an overly loose match risks grabbing the wrong series.
        // Which elements are checked is controlled by
        // AnimeSiteSettings.SeriesLinkSelector.
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

        // URL scheme for finding an episode link is now driven by
        // AnimeSiteSettings.EpisodeUrlPattern (default matches
        // "...-episode-<N>-..." like animexin.dev/donghuaworld.com use);
        // which elements are checked is controlled by
        // AnimeSiteSettings.EpisodeLinkSelector.
        private string FindEpisodeLink(string animeLink, int absoluteEpisodeNumber)
        {
            var response = _httpClient.Get(new HttpRequest(animeLink));
            var doc = ParseHtml(response.Content);

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
