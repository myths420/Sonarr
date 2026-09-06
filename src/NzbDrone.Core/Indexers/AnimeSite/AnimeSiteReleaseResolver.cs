using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using Jint;
using NLog;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // One real, directly-fetchable download link resolved for a single
    // episode -- deliberately just {Title, Url}, the same shape ReleaseInfo
    // needs, but without any of ReleaseInfo's indexer/protocol baggage so
    // this can be used outside the indexer search pipeline too.
    public class ResolvedRelease
    {
        public string Title { get; set; }
        public string Url { get; set; }
    }

    // The handful of AnimeSiteSettings fields release resolution actually
    // needs, bundled so callers outside AnimeSiteIndexer (the Sites
    // catalogue's download panel, which reads a different site's settings --
    // an Import List, not this Indexer) don't need the whole settings type.
    public class AnimeSiteReleaseOptions
    {
        public string[] DirectDownloadHosts { get; set; }
        public string DownloadLinkSelector { get; set; }
        public List<LinkResolutionRule> ResolutionRules { get; set; }
        public string ScrapingScript { get; set; }
        public AnimeSiteFetchOptions Fetch { get; set; } = AnimeSiteFetchOptions.Direct;

        public static AnimeSiteReleaseOptions FromSettings(AnimeSiteSettings settings)
        {
            return new AnimeSiteReleaseOptions
            {
                DirectDownloadHosts = settings.GetDirectDownloadHostsArray(),
                DownloadLinkSelector = settings.GetDownloadLinkSelector(),
                ResolutionRules = settings.GetLinkResolutionRules(),
                ScrapingScript = settings.ScrapingScript,
                Fetch = AnimeSiteFetchOptions.FromSettings(settings)
            };
        }
    }

    public interface IAnimeSiteReleaseResolver
    {
        List<ResolvedRelease> GetReleases(AnimeSiteReleaseOptions options, string episodeHtml, string episodeUrl, string seriesTitle, int episodeNumber, Logger logger);
    }

    // Extracted out of AnimeSiteParser so the same "turn an episode page
    // into real, directly-fetchable download links" logic (landing-page
    // hops, host allowlist, the script's getReleases()) can be reused
    // outside the indexer search pipeline -- namely the Sites catalogue's
    // download panel, which has no Series/Episode library entry to search
    // against and so can never go through AnimeSiteIndexer at all.
    public class AnimeSiteReleaseResolver : IAnimeSiteReleaseResolver
    {
        private readonly IAnimeSiteFetcher _fetcher;

        public AnimeSiteReleaseResolver(IAnimeSiteFetcher fetcher)
        {
            _fetcher = fetcher;
        }

        public List<ResolvedRelease> GetReleases(AnimeSiteReleaseOptions options, string episodeHtml, string episodeUrl, string seriesTitle, int episodeNumber, Logger logger)
        {
            return !string.IsNullOrWhiteSpace(options.ScrapingScript)
                ? GetReleasesViaScript(options, episodeHtml, episodeUrl, seriesTitle, episodeNumber, logger)
                : GetReleasesViaSelectors(options, episodeHtml, episodeUrl, seriesTitle, episodeNumber, logger);
        }

        // Script contract: getReleases(episodeHtml, episodeUrl, seriesTitle,
        // episodeNumber) -> JSON.stringify()'d array of {title, url}. See
        // AnimeSiteSettings.ScrapingScript's help text for the full contract
        // (findSeriesUrl/findEpisodeUrl are indexer-search-only and not
        // needed here -- the caller already has the episode page in hand).
        private List<ResolvedRelease> GetReleasesViaScript(AnimeSiteReleaseOptions options, string episodeHtml, string episodeUrl, string seriesTitle, int episodeNumber, Logger logger)
        {
            var releases = new List<ResolvedRelease>();
            var host = new AnimeSiteScriptHost(_fetcher, options.Fetch, logger);
            var allowedHostsJson = JsonSerializer.Serialize(options.DirectDownloadHosts ?? System.Array.Empty<string>());

            try
            {
                // Generous timeout: a script that follows landing-page hops
                // through a headless browser can spend several seconds per
                // fetch, and this only runs on a user-initiated resolve.
                var engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(120)));
                engine.SetValue("host", host);
                engine.Execute(options.ScrapingScript);

                // arg 5 (allowedHosts) added so a script can honour the
                // indexer's Direct Download Hosts field instead of
                // hard-coding the list; older 4-arg scripts ignore it.
                var json = engine.Invoke("getReleases", episodeHtml, episodeUrl, seriesTitle, episodeNumber, allowedHostsJson).AsString();
                var scriptReleases = JsonSerializer.Deserialize<List<ResolvedRelease>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<ResolvedRelease>();

                foreach (var sr in scriptReleases)
                {
                    if (string.IsNullOrEmpty(sr.Url))
                    {
                        continue;
                    }

                    releases.Add(new ResolvedRelease
                    {
                        Title = !string.IsNullOrEmpty(sr.Title) ? sr.Title : $"{seriesTitle} - Episode {episodeNumber:000}",
                        Url = sr.Url
                    });
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Scraping script getReleases() failed for {0}", seriesTitle);
            }

            return releases;
        }

        // Port of main.py's _extract_any_download_links: grab any link on
        // the episode page pointing at a known direct-download host, then
        // run each one through ResolutionRules (Mediafire's landing-page hop,
        // mirrored.to's dl=0->dl=1, etc.) so the returned Url is already a
        // real, directly-fetchable file link.
        private List<ResolvedRelease> GetReleasesViaSelectors(AnimeSiteReleaseOptions options, string episodeHtml, string episodeUrl, string seriesTitle, int episodeNumber, Logger logger)
        {
            var releases = new List<ResolvedRelease>();
            var directDlHosts = options.DirectDownloadHosts ?? Array.Empty<string>();
            var doc = ParseHtml(episodeHtml);
            var seen = new HashSet<string>();

            foreach (var a in doc.QuerySelectorAll(options.DownloadLinkSelector))
            {
                var href = a.GetAttribute("href");
                if (string.IsNullOrEmpty(href) || !href.StartsWith("http") || !seen.Add(href))
                {
                    continue;
                }

                if (!directDlHosts.Any(h => href.Contains(h)))
                {
                    continue;
                }

                var host = directDlHosts.First(h => href.Contains(h));
                var resolvedUrl = ApplyResolutionRules(href, options.ResolutionRules ?? new List<LinkResolutionRule>(), options.Fetch, logger);

                releases.Add(new ResolvedRelease
                {
                    Title = $"{seriesTitle} - Episode {episodeNumber:000} [{host}]",
                    Url = resolvedUrl
                });
            }

            return releases;
        }

        private string ApplyResolutionRules(string url, List<LinkResolutionRule> resolutionRules, AnimeSiteFetchOptions fetch, Logger logger)
        {
            const int maxHops = 5;
            for (var hop = 0; hop < maxHops; hop++)
            {
                var rule = resolutionRules.FirstOrDefault(r => !string.IsNullOrEmpty(r.HostContains) && url.Contains(r.HostContains));
                if (rule == null)
                {
                    break;
                }

                if (!string.IsNullOrEmpty(rule.ResolveSelector))
                {
                    var resolved = ResolveViaSelector(url, rule.ResolveSelector, fetch, logger);
                    if (string.IsNullOrEmpty(resolved) || resolved == url)
                    {
                        break;
                    }

                    url = resolved;
                }
                else if (!string.IsNullOrEmpty(rule.UrlReplaceFrom))
                {
                    var replaced = url.Replace(rule.UrlReplaceFrom, rule.UrlReplaceTo ?? "");
                    if (replaced == url)
                    {
                        break;
                    }

                    url = replaced;
                }
                else
                {
                    break;
                }
            }

            return url;
        }

        private string ResolveViaSelector(string landingUrl, string selector, AnimeSiteFetchOptions fetch, Logger logger)
        {
            try
            {
                var content = _fetcher.GetHtml(landingUrl, null, fetch);
                var doc = ParseHtml(content);
                var element = doc.QuerySelector(selector);
                var href = element?.GetAttribute("href");
                return !string.IsNullOrEmpty(href) && href.StartsWith("http") ? href : null;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to resolve link via selector '{0}' on {1}", selector, landingUrl);
                return null;
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
