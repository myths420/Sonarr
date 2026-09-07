using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        // Hosts that are never worth handing to the downloader:
        //  - TeraBox: real file only downloads through its desktop app
        //  - mirrored.to: a timed multi-host redirect page; the hosts it
        //    points to are ~all dead, and the copies that do work are raw
        //    rips with no English subs.
        // Dropping these leaves Mediafire (the site's own subbed encode)
        // and the Dailymotion embed, which is what actually works.
        private static readonly string[] SkipHosts =
        {
            "terabox", "1024tera", "teraboxapp", "teraboxlink", "terasharelink",
            "4funbox", "mirrobox", "nephobox", "momerybox", "freeterabox",
            "mirrored.to", "mirrorace.com", "mir.cr"
        };

        private static readonly Regex DailymotionId = new Regex(
            @"dailymotion\.com/(?:embed/)?video/([A-Za-z0-9]+)|dai\.ly/([A-Za-z0-9]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The "Select Video Server" <option>s: a base64 iframe blob + a
        // human label like "Hardsub English Dailymotion".
        private static readonly Regex ServerOption = new Regex(
            @"<option[^>]*\svalue=""([A-Za-z0-9+/=]{16,})""[^>]*>([^<]+)</option>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IframeSrc = new Regex(
            @"src=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EnglishLabel = new Regex(
            @"\b(english|eng[\s-]?sub|all[\s-]?sub|multi[\s-]?sub|multiple[\s-]?sub)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IndonesianLabel = new Regex(
            @"\b(indonesian?|indo[\s-]?sub|sub[\s-]?indo)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public List<ResolvedRelease> GetReleases(AnimeSiteReleaseOptions options, string episodeHtml, string episodeUrl, string seriesTitle, int episodeNumber, Logger logger)
        {
            var releases = !string.IsNullOrWhiteSpace(options.ScrapingScript)
                ? GetReleasesViaScript(options, episodeHtml, episodeUrl, seriesTitle, episodeNumber, logger)
                : GetReleasesViaSelectors(options, episodeHtml, episodeUrl, seriesTitle, episodeNumber, logger);

            var kept = releases
                .Where(r => string.IsNullOrEmpty(r.Url) ||
                            !SkipHosts.Any(h => r.Url.Contains(h, StringComparison.OrdinalIgnoreCase)))
                .Where(r => !IsForeignLanguage(r.Title))
                .ToList();

            if (kept.Count != releases.Count)
            {
                logger.Debug("Filtered {0} of {1} release(s) for {2} episode {3} (skip-host or non-English sub)", releases.Count - kept.Count, releases.Count, seriesTitle, episodeNumber);
            }

            // The episode's real video is a Dailymotion embed. These sites
            // list one per sub language ("Hardsub English Dailymotion" vs
            // "Hardsub Indonesia Dailymotion"); pick the English one and
            // prefer it -- the right language, reliably up, no login.
            kept.InsertRange(0, DailymotionReleases(options.Fetch, episodeHtml, episodeUrl, seriesTitle, episodeNumber, logger));

            return kept;
        }

        // Extract a Sonarr-parseable quality token from a scraper's own
        // release title (e.g. "... - 1080p - English - [mediafire.com]" or
        // "... [Dailymotion] 1080p WEB-DL"). Null when nothing recognised.
        public static string QualityTag(string scraperTitle)
        {
            if (string.IsNullOrEmpty(scraperTitle))
            {
                return null;
            }

            var web = scraperTitle.Contains("WEB", StringComparison.OrdinalIgnoreCase);

            string res = null;
            if (scraperTitle.Contains("2160") || scraperTitle.Contains("4K", StringComparison.OrdinalIgnoreCase))
            {
                res = "2160p";
            }
            else if (scraperTitle.Contains("1080"))
            {
                res = "1080p";
            }
            else if (scraperTitle.Contains("720"))
            {
                res = "720p";
            }
            else if (scraperTitle.Contains("480"))
            {
                res = "480p";
            }

            if (res == null)
            {
                return null;
            }

            return web ? $"WEBDL-{res}" : res;
        }

        // A release the scraper labelled with a non-English sub language
        // (e.g. "... - 1080p - Indonesian - [mediafire.com]"). This fork is
        // English-only, so those are dropped outright.
        private static bool IsForeignLanguage(string title)
        {
            return !string.IsNullOrEmpty(title)
                && IndonesianLabel.IsMatch(title)
                && !EnglishLabel.IsMatch(title);
        }

        private static IEnumerable<ResolvedRelease> DailymotionReleases(AnimeSiteFetchOptions fetch, string episodeHtml, string episodeUrl, string seriesTitle, int episodeNumber, Logger logger)
        {
            if (string.IsNullOrEmpty(episodeHtml))
            {
                yield break;
            }

            if (fetch == null || !fetch.UsesResolver)
            {
                if (episodeHtml.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase))
                {
                    logger.Warn("{0} episode {1} has a Dailymotion embed but this indexer has no Page Resolver URL set -- set it (e.g. http://page-resolver:3000) to download from Dailymotion.", seriesTitle, episodeNumber);
                }

                yield break;
            }

            var baseUrl = fetch.PageResolverUrl.TrimEnd('/');
            var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sawLabelledServers = false;

            // Labelled server <option>s first (base64 iframe blobs).
            foreach (Match opt in ServerOption.Matches(episodeHtml))
            {
                string decoded;
                try
                {
                    decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(opt.Groups[1].Value));
                }
                catch (FormatException)
                {
                    continue;
                }

                var src = IframeSrc.Match(decoded);
                if (!src.Success)
                {
                    continue;
                }

                sawLabelledServers = true;

                var dm = DailymotionId.Match(src.Groups[1].Value);
                if (!dm.Success)
                {
                    continue;
                }

                var id = dm.Groups[1].Success ? dm.Groups[1].Value : dm.Groups[2].Value;
                var label = opt.Groups[2].Value.Trim();
                var isIndo = IndonesianLabel.IsMatch(label);
                var isEng = EnglishLabel.IsMatch(label);

                // 3 = clearly English, 2 = "all sub" style, drop pure Indonesian.
                var score = isEng && !isIndo ? 3 : isEng ? 2 : isIndo ? -1 : 1;
                if (score < 0)
                {
                    continue;
                }

                if (!byId.TryGetValue(id, out var existing) || score > existing)
                {
                    byId[id] = score;
                }
            }

            // Fall back to any bare (already-decoded) Dailymotion embed on
            // the page -- but only when there were no labelled servers at
            // all. If there were and none was English, the bare embed is
            // just the (wrong-language) default player; don't grab it.
            if (byId.Count == 0 && !sawLabelledServers)
            {
                foreach (Match m in DailymotionId.Matches(episodeHtml))
                {
                    var id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (!string.IsNullOrEmpty(id))
                    {
                        byId.TryAdd(id, 0);
                    }
                }
            }

            foreach (var kv in byId.OrderByDescending(k => k.Value))
            {
                logger.Debug("Dailymotion embed {0} (score {1}) for {2} episode {3}", kv.Key, kv.Value, seriesTitle, episodeNumber);

                var url = $"{baseUrl}/dailymotion/fetch?v={Uri.EscapeDataString(kv.Key)}";
                if (!string.IsNullOrEmpty(episodeUrl))
                {
                    url += $"&referer={Uri.EscapeDataString(episodeUrl)}";
                }

                yield return new ResolvedRelease
                {
                    // "1080p WEB-DL" so both release paths parse a real
                    // quality (the stream is remuxed up to 1080p).
                    Title = $"{seriesTitle} - Episode {episodeNumber:000} [Dailymotion] 1080p WEB-DL",
                    Url = url
                };
            }
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
                // Long timeout: getReleases() may follow several landing-page
                // hops, and each one can be a slow headless-browser fetch.
                var engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromMinutes(5)));
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
