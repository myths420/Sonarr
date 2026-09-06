using System;
using System.Collections.Generic;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using Jint;
using NLog;
using NzbDrone.Core.Indexers.AnimeSite;

namespace NzbDrone.Core.ImportLists.AnimeSite
{
    // One show found by the catalogue script. Url is the show's page.
    public class AnimeSiteCatalogEntry
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public int Year { get; set; }
        public int TvdbId { get; set; }
        public int MalId { get; set; }
        public string ImdbId { get; set; }
    }

    // One episode found by the catalogue script's listEpisodes().
    public class AnimeSiteEpisodeEntry
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
    }

    public interface IAnimeSiteCatalogBrowser
    {
        List<AnimeSiteCatalogEntry> Browse(AnimeSiteCatalogueOptions options, Logger logger);

        // Script-only. Returns an empty list if no catalogue script is set.
        List<AnimeSiteEpisodeEntry> BrowseEpisodes(AnimeSiteCatalogueOptions options, string showUrl, Logger logger);
    }

    public class AnimeSiteCatalogBrowser : IAnimeSiteCatalogBrowser
    {
        private readonly IAnimeSiteFetcher _fetcher;

        public AnimeSiteCatalogBrowser(IAnimeSiteFetcher fetcher)
        {
            _fetcher = fetcher;
        }

        public List<AnimeSiteCatalogEntry> Browse(AnimeSiteCatalogueOptions options, Logger logger)
        {
            return string.IsNullOrWhiteSpace(options.ScrapingScript)
                ? BrowseViaSelectors(options, logger)
                : BrowseViaScript(options, logger);
        }

        // Walk BrowsePathPattern pages, pulling show name from each matched
        // link's title (or text). Stops at MaxPages or the first empty page.
        private List<AnimeSiteCatalogEntry> BrowseViaSelectors(AnimeSiteCatalogueOptions options, Logger logger)
        {
            var baseUrl = (options.BaseUrl ?? string.Empty).TrimEnd('/');
            var selector = options.GetSeriesLinkSelector();
            var seen = new HashSet<string>();
            var shows = new List<AnimeSiteCatalogEntry>();

            for (var page = 1; page <= options.MaxPages; page++)
            {
                var path = options.BrowsePathPattern.Replace("{page}", page.ToString());
                var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : baseUrl + (path.StartsWith('/') ? path : "/" + path);

                string content;
                try
                {
                    content = _fetcher.GetHtml(url, null, options.Fetch);
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "AnimeSite catalogue browser: stopping paging, failed to fetch {0}", url);
                    break;
                }

                var doc = ParseHtml(content);
                var newThisPage = 0;

                foreach (var a in doc.QuerySelectorAll(selector))
                {
                    var href = a.GetAttribute("href");
                    if (string.IsNullOrWhiteSpace(href) || !seen.Add(href))
                    {
                        continue;
                    }

                    var title = a.GetAttribute("title");
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = a.TextContent?.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    shows.Add(new AnimeSiteCatalogEntry { Title = title, Url = href });
                    newThisPage++;
                }

                if (newThisPage == 0)
                {
                    break;
                }
            }

            return shows;
        }

        // Runs listShows() from the catalogue script.
        private List<AnimeSiteCatalogEntry> BrowseViaScript(AnimeSiteCatalogueOptions options, Logger logger)
        {
            var host = new AnimeSiteScriptHost(_fetcher, options.Fetch, logger);
            var baseUrl = (options.BaseUrl ?? string.Empty).TrimEnd('/');

            var engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(60)));
            engine.SetValue("host", host);
            engine.Execute(options.ScrapingScript);

            var json = engine.Invoke("listShows", baseUrl, options.MaxPages).AsString();
            logger.Info("AnimeSite catalogue browser: listShows() returned {0} bytes of JSON for {1}", json?.Length ?? -1, baseUrl);

            return JsonSerializer.Deserialize<List<AnimeSiteCatalogEntry>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new List<AnimeSiteCatalogEntry>();
        }

        public List<AnimeSiteEpisodeEntry> BrowseEpisodes(AnimeSiteCatalogueOptions options, string showUrl, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(options.ScrapingScript))
            {
                return new List<AnimeSiteEpisodeEntry>();
            }

            var host = new AnimeSiteScriptHost(_fetcher, options.Fetch, logger);

            try
            {
                var showHtml = host.Get(showUrl);

                var engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(30)));
                engine.SetValue("host", host);
                engine.Execute(options.ScrapingScript);

                var json = engine.Invoke("listEpisodes", showHtml, showUrl).AsString();

                return JsonSerializer.Deserialize<List<AnimeSiteEpisodeEntry>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new List<AnimeSiteEpisodeEntry>();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "AnimeSite catalogue browser: failed to list episodes for {0}", showUrl);
                return new List<AnimeSiteEpisodeEntry>();
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
