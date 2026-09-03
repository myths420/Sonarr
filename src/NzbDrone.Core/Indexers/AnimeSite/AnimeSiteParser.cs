using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // SCOPE OF THIS FILE (matches the phasing in DirectHttpDownloadClient):
    // only the direct-download-link path (mediafire/mirrored.to/terabox) is
    // implemented. If an episode page only has video-player embeds
    // (Dailymotion/Rumble/gdriveplayer/etc, the hls.py path) this returns no
    // results for it rather than a release that would fail to download --
    // porting that embed-resolution logic is the next piece, not yet here.
    //
    // The episode-URL regex and the direct-download host list used to be
    // hardcoded `static readonly` fields here -- they're now passed in from
    // AnimeSiteSettings (via AnimeSiteIndexer), so a new "anime site"
    // instance with a different URL scheme or host preferences doesn't need
    // a code change, just different values in those two settings fields.
    public class AnimeSiteParser : IParseIndexerResponse
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly Regex _episodeUrlRegex;
        private readonly string[] _directDlHosts;
        private readonly string _seriesLinkSelector;
        private readonly string _episodeLinkSelector;
        private readonly string _downloadLinkSelector;

        public AnimeSiteParser(IHttpClient httpClient, Logger logger, int absoluteEpisodeNumber, string seriesTitle, Regex episodeUrlRegex, string[] directDlHosts, string seriesLinkSelector, string episodeLinkSelector, string downloadLinkSelector)
        {
            _httpClient = httpClient;
            _logger = logger;
            AbsoluteEpisodeNumber = absoluteEpisodeNumber;
            SeriesTitle = seriesTitle;
            _episodeUrlRegex = episodeUrlRegex;
            _directDlHosts = directDlHosts;
            _seriesLinkSelector = seriesLinkSelector;
            _episodeLinkSelector = episodeLinkSelector;
            _downloadLinkSelector = downloadLinkSelector;
        }

        // Set by AnimeSiteIndexer right before each Fetch() call -- see the
        // comment there for why this can't just come through ParseResponse's
        // IndexerResponse parameter.
        public int AbsoluteEpisodeNumber { get; set; }

        public string SeriesTitle { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
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

                releases.AddRange(ScrapeEpisodeDownloadLinks(episodeLink));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to parse AnimeSite response for {0}", indexerResponse.Request.Url);
            }

            return releases;
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

        // Port of main.py's _extract_any_download_links: grab any link on
        // the episode page pointing at a known direct-download host (host
        // list from AnimeSiteSettings.DirectDownloadHosts, which elements
        // are checked from AnimeSiteSettings.DownloadLinkSelector).
        private IEnumerable<ReleaseInfo> ScrapeEpisodeDownloadLinks(string episodeLink)
        {
            var response = _httpClient.Get(new HttpRequest(episodeLink));
            var doc = ParseHtml(response.Content);
            var seen = new HashSet<string>();

            foreach (var a in doc.QuerySelectorAll(_downloadLinkSelector))
            {
                var href = a.GetAttribute("href");
                if (string.IsNullOrEmpty(href) || !href.StartsWith("http") || !seen.Add(href))
                {
                    continue;
                }

                if (!_directDlHosts.Any(host => href.Contains(host)))
                {
                    continue;
                }

                var host = _directDlHosts.First(h => href.Contains(h));
                yield return new ReleaseInfo
                {
                    Guid = href,
                    Title = $"{SeriesTitle} - Episode {AbsoluteEpisodeNumber:000} [{host}]",
                    DownloadUrl = href,
                    InfoUrl = episodeLink,
                    Size = 0,
                    PublishDate = DateTime.UtcNow,
                    DownloadProtocol = Indexers.DownloadProtocol.Unknown,
                };
            }
        }

        private static IDocument ParseHtml(string html)
        {
            var config = AngleSharp.Configuration.Default;
            var context = BrowsingContext.New(config);
            return context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();
        }
    }
}
