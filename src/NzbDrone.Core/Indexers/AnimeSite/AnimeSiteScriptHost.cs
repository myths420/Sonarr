using System;
using System.Collections.Generic;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // Exposed to the per-indexer JavaScript scraping script (AnimeSiteSettings.
    // ScrapingScript) as the global `host` object. This is what gives a
    // script full "write your own scraper" power -- fetch any page, run any
    // CSS selector against it -- matching what the original Python version
    // (requests + BeautifulSoup) could do, without needing a real browser.
    //
    // Every method here only ever passes plain strings across the JS/C#
    // boundary (HTML in, JSON out) rather than trying to hand JS a live
    // .NET object graph -- CLR-object interop shape/behavior varies between
    // scripting-engine versions, but string marshalling and JSON.parse/
    // JSON.stringify are basic, stable JavaScript, so this is the most
    // dependable boundary to build on. Scripts call JSON.parse() on
    // host.select()/host.selectOne()'s return value.
    public class AnimeSiteScriptHost
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public AnimeSiteScriptHost(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        // host.get(url) -> raw HTML string of that page (or "" on failure).
        public string Get(string url)
        {
            try
            {
                return _httpClient.Get(new HttpRequest(url)).Content;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Scraping script host.get() failed for {0}", url);
                return "";
            }
        }

        // host.select(html, cssSelector) -> JSON array string of
        // {text, href, title} for every matching element. href/title are ""
        // for elements without that attribute. title is included because
        // listing-page thumbnails are often <a title="Show Name"><img></a>
        // with no usable text content.
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

        // host.selectHtml(html, cssSelector) -> JSON array of the outer HTML
        // of every matching element, as strings. Lets a script pull out a
        // repeated block (e.g. one per quality/language on a download page)
        // and then run host.select/selectOne again against just that block's
        // HTML to read its own nested label/links -- host.select() alone
        // only returns flat {text,href,title} per element, with no way to
        // keep a block's children grouped together.
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

        // host.log(message) -- writes to Sonarr's debug log, for
        // troubleshooting a script directly from Settings > System > Logs.
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
