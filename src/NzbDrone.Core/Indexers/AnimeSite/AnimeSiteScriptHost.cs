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

        // host.get(url[, referer]) -> page HTML, or "" on failure. Routed
        // through the site's headless browser / session exactly like the
        // fetches AnimeSite makes itself -- so in Always mode a sub-page or
        // an off-site download link a script follows goes through
        // Byparr/FlareSolverr too, not a raw request that Cloudflare blocks.
        public string Get(string url)
        {
            return Get(url, null);
        }

        public string Get(string url, string referer)
        {
            try
            {
                var page = _fetcher.GetPage(url, string.IsNullOrEmpty(referer) ? null : referer, _fetch);

                if (string.IsNullOrEmpty(page.Html) && _fetch.Mode == AnimeSiteBrowserMode.Always)
                {
                    _logger.Warn("Scraping script host.get(): headless browser returned nothing for {0}", url);
                }

                return page.Html ?? "";
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.get() failed for {0}", url);
                return "";
            }
        }

        // host.getPage(url[, referer]) -> JSON {html, finalUrl}. finalUrl is
        // where the fetch actually landed (the headless browser reports the
        // post-redirect URL) -- use it to follow a shortener or a
        // redirect-only external download link.
        public string GetPage(string url)
        {
            return GetPage(url, null);
        }

        public string GetPage(string url, string referer)
        {
            try
            {
                var page = _fetcher.GetPage(url, string.IsNullOrEmpty(referer) ? null : referer, _fetch);
                return JsonSerializer.Serialize(new { html = page.Html ?? "", finalUrl = page.FinalUrl ?? url });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.getPage() failed for {0}", url);
                return JsonSerializer.Serialize(new { html = "", finalUrl = url });
            }
        }

        // host.resolveUrl(url) -> the URL the fetch ends up on after
        // redirects, routed through the headless browser in Always mode.
        // Returns the input url unchanged on failure.
        public string ResolveUrl(string url)
        {
            try
            {
                var page = _fetcher.GetPage(url, null, _fetch);
                return string.IsNullOrWhiteSpace(page.FinalUrl) ? url : page.FinalUrl;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.resolveUrl() failed for {0}", url);
                return url;
            }
        }

        // host.post(url, body) -> response body ("" on failure). body is a
        // url-encoded string (e.g. "a=1&b=2"). Routed through the headless
        // browser (FlareSolverr request.post) whenever one is configured, so
        // a POST that needs the Cloudflare-cleared browser session -- like a
        // file host's "generate download link" endpoint -- runs in it.
        public string Post(string url, string body)
        {
            try
            {
                var page = _fetcher.PostPage(url, body ?? string.Empty, _fetch);
                return page.Html ?? "";
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.post() failed for {0}", url);
                return "";
            }
        }

        // host.resolvePage(url[, clickText[, resultSelector]]) -> JSON
        // {link, filename, error}. Drives the page-resolver service (a real
        // headless browser that can click) -- for a download page that only
        // reveals its link after a button press, e.g. misterdonghua.in's
        // "Get Video". Needs "Page Resolver URL" set on the indexer.
        public string ResolvePage(string url)
        {
            return ResolvePage(url, null, null);
        }

        public string ResolvePage(string url, string clickText)
        {
            return ResolvePage(url, clickText, null);
        }

        public string ResolvePage(string url, string clickText, string resultSelector)
        {
            try
            {
                var resolved = _fetcher.ResolvePage(url, clickText, resultSelector, _fetch);
                if (!string.IsNullOrEmpty(resolved.Error))
                {
                    _logger.Warn("Scraping script host.resolvePage() for {0}: {1}", url, resolved.Error);
                }

                return JsonSerializer.Serialize(new
                {
                    link = resolved.Link ?? "",
                    filename = resolved.Filename ?? "",
                    error = resolved.Error ?? ""
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.resolvePage() failed for {0}", url);
                return JsonSerializer.Serialize(new { link = "", filename = "", error = ex.Message });
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
