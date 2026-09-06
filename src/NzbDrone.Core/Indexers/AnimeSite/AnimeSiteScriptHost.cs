using System;
using System.Collections.Generic;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using NLog;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // The global `host` object exposed to the per-indexer Scraping Script
    // and Catalogue Script. Every method takes and returns plain strings
    // (HTML in, JSON out); scripts JSON.parse() the select* results.
    public class AnimeSiteScriptHost
    {
        private readonly IAnimeSiteFetcher _fetcher;
        private readonly AnimeSiteFetchOptions _fetch;
        private readonly Logger _logger;

        public AnimeSiteScriptHost(IAnimeSiteFetcher fetcher, AnimeSiteFetchOptions fetch, Logger logger)
        {
            _fetcher = fetcher;
            _fetch = fetch ?? AnimeSiteFetchOptions.Direct;
            _logger = logger;
        }

        // host.get(url) -> page HTML, or "" on failure. Uses the site's
        // headless-browser / session options when configured.
        public string Get(string url)
        {
            try
            {
                return _fetcher.GetHtml(url, null, _fetch) ?? "";
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.get() failed for {0}", url);
                return "";
            }
        }

        // host.select(html, cssSelector) -> JSON array of {text, href, title}
        // for every matching element (missing attributes come back as "").
        public string Select(string html, string selector)
        {
            var results = new List<ScriptElement>();
            try
            {
                var doc = ParseHtml(html);
                foreach (var el in doc.QuerySelectorAll(selector))
                {
                    results.Add(new ScriptElement { text = el.TextContent?.Trim() ?? "", href = el.GetAttribute("href") ?? "", title = el.GetAttribute("title") ?? "" });
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.select() failed for selector '{0}'", selector);
            }

            return JsonSerializer.Serialize(results);
        }

        // host.selectHtml(html, cssSelector) -> JSON array of each matching
        // element's outer HTML, for re-running host.select against a block.
        public string SelectHtml(string html, string selector)
        {
            var results = new List<string>();
            try
            {
                var doc = ParseHtml(html);
                foreach (var el in doc.QuerySelectorAll(selector))
                {
                    results.Add(el.OuterHtml);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.selectHtml() failed for selector '{0}'", selector);
            }

            return JsonSerializer.Serialize(results);
        }

        // host.selectOne(html, cssSelector) -> JSON object string
        // {text, href} for the first matching element, or the literal
        // string "null" if nothing matched (JSON.parse("null") === null).
        public string SelectOne(string html, string selector)
        {
            try
            {
                var doc = ParseHtml(html);
                var el = doc.QuerySelector(selector);
                if (el == null)
                {
                    return "null";
                }

                return JsonSerializer.Serialize(new ScriptElement { text = el.TextContent?.Trim() ?? "", href = el.GetAttribute("href") ?? "", title = el.GetAttribute("title") ?? "" });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.selectOne() failed for selector '{0}'", selector);
                return "null";
            }
        }

        // host.log(message) -> writes to the debug log.
        public void Log(string message)
        {
            _logger.Debug("[Scraping script] {0}", message);
        }

        private static IDocument ParseHtml(string html)
        {
            var config = AngleSharp.Configuration.Default;
            var context = BrowsingContext.New(config);
            return context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();
        }

        private class ScriptElement
        {
            public string text { get; set; }
            public string href { get; set; }
            public string title { get; set; }
        }
    }
}
