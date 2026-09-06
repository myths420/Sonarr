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
    // A resolved download link for one episode.
    public class ResolvedRelease
    {
        public string Title { get; set; }
        public string Url { get; set; }
    }

    // The AnimeSiteSettings fields release resolution needs.
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

    // Turns an episode page into download links: getReleases() script, or
    // the DownloadLinkSelector + host allowlist + LinkResolutionRules.
    // Shared by the indexer search and the Sites catalogue.
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

        // getReleases(episodeHtml, episodeUrl, seriesTitle, episodeNumber,
        // allowedHostsJson) -> JSON array of {title, url}.
        private List<ResolvedRelease> GetReleasesViaScript(AnimeSiteReleaseOptions options, string episodeHtml, string episodeUrl, string seriesTitle, int episodeNumber, Logger logger)
        {
            var releases = new List<ResolvedRelease>();
            var host = new AnimeSiteScriptHost(_fetcher, options.Fetch, logger);
            var allowedHostsJson = JsonSerializer.Serialize(options.DirectDownloadHosts ?? System.Array.Empty<string>());

            try
            {
                // Long timeout: a script may follow landing-page hops.
                var engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(120)));
                engine.SetValue("host", host);
                engine.Execute(options.ScrapingScript);

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

        // Every DownloadLinkSelector match pointing at an allowed host, run
        // through the LinkResolutionRules.
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
