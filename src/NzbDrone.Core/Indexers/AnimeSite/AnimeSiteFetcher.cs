using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    public enum AnimeSiteBrowserMode
    {
        Off = 0,
        Auto = 1,
        Always = 2
    }

    // Per-site fetch settings, built from AnimeSiteSettings.
    public class AnimeSiteFetchOptions
    {
        public string HeadlessUrl { get; set; }
        public AnimeSiteBrowserMode Mode { get; set; }
        public IndexerSessionConfig Session { get; set; }

        // page-resolver service (distribution/page-resolver) -- a real
        // headless browser that can click. For download pages that only
        // hand over the link after a button press (misterdonghua.in).
        public string PageResolverUrl { get; set; }

        public static readonly AnimeSiteFetchOptions Direct = new AnimeSiteFetchOptions();

        public bool UsesHeadless => Mode != AnimeSiteBrowserMode.Off && !string.IsNullOrWhiteSpace(HeadlessUrl);

        public bool UsesSession => Session is { IsConfigured: true };

        public bool UsesResolver => !string.IsNullOrWhiteSpace(PageResolverUrl);

        public static AnimeSiteFetchOptions FromSettings(AnimeSiteSettings settings)
        {
            return new AnimeSiteFetchOptions
            {
                HeadlessUrl = settings.HeadlessBrowserUrl,
                Mode = (AnimeSiteBrowserMode)settings.HeadlessBrowserMode,
                Session = IndexerSessionConfig.FromSettings(settings),
                PageResolverUrl = settings.PageResolverUrl
            };
        }
    }

    // A download link a page-resolver dug out by driving a real browser.
    public class AnimeSiteResolvedLink
    {
        public string Link { get; set; } = string.Empty;
        public string Filename { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    // The result of a fetch: the page HTML plus the URL it actually ended
    // up on (after redirects / a headless-browser navigation). FinalUrl is
    // what a Scraping Script needs to follow a shortener or a redirect-only
    // external download link -- host.get() alone only hands back HTML.
    public class AnimeSitePage
    {
        public string Html { get; set; } = string.Empty;
        public string FinalUrl { get; set; } = string.Empty;

        public static readonly AnimeSitePage Empty = new AnimeSitePage();
    }

    // Fetches page HTML, optionally through a FlareSolverr-compatible
    // headless browser (Headless Browser URL setting). In Always mode every
    // fetch here -- the site's own pages, sub-pages, and off-site download
    // links a Scraping Script follows -- is routed through that browser.
    public interface IAnimeSiteFetcher
    {
        string GetHtml(string url, string referer, AnimeSiteFetchOptions fetch);

        AnimeSitePage GetPage(string url, string referer, AnimeSiteFetchOptions fetch);

        // POST a url-encoded body. Routed through the headless browser
        // (FlareSolverr request.post) whenever one is configured -- a POST
        // that depends on a Cloudflare-cleared session (e.g. a file host's
        // "give me the link" endpoint) has to run in the same browser
        // context that cleared the challenge.
        AnimeSitePage PostPage(string url, string postData, AnimeSiteFetchOptions fetch);

        // Hands a URL to the page-resolver service (a real, click-capable
        // headless browser) and gets back the download link it produced.
        // Needs PageResolverUrl configured; otherwise returns an error.
        AnimeSiteResolvedLink ResolvePage(string url, string clickText, string resultSelector, AnimeSiteFetchOptions fetch);
    }

    public class AnimeSiteFetcher : IAnimeSiteFetcher
    {
        private readonly IHttpClient _httpClient;
        private readonly IAnimeSiteSessionClient _sessionClient;
        private readonly Logger _logger;

        public AnimeSiteFetcher(IHttpClient httpClient, IAnimeSiteSessionClient sessionClient, Logger logger)
        {
            _httpClient = httpClient;
            _sessionClient = sessionClient;
            _logger = logger;
        }

        public string GetHtml(string url, string referer, AnimeSiteFetchOptions fetch)
        {
            return GetPage(url, referer, fetch).Html;
        }

        public AnimeSitePage GetPage(string url, string referer, AnimeSiteFetchOptions fetch)
        {
            fetch ??= AnimeSiteFetchOptions.Direct;

            // Always: route every fetch -- the site, its sub-pages, and any
            // off-site link a script follows -- through the headless browser.
            // Direct is only a last resort if the browser itself is down.
            if (fetch.UsesHeadless && fetch.Mode == AnimeSiteBrowserMode.Always)
            {
                return Headless(url, fetch) ?? Direct(url, referer, fetch);
            }

            var page = Direct(url, referer, fetch);

            // Auto: fall back to the headless browser on any fetch that comes
            // back as a Cloudflare challenge OR empty (a reset / silent block).
            // This runs per fetch, so a sub-page or an off-site link a script
            // pulls with host.get() gets the same treatment as the first page.
            if (fetch.UsesHeadless && fetch.Mode == AnimeSiteBrowserMode.Auto &&
                (LooksBlocked(page.Html) || string.IsNullOrWhiteSpace(page.Html)))
            {
                _logger.Debug("AnimeSite: direct fetch of {0} was blocked or empty, retrying via headless browser", url);
                return Headless(url, fetch) ?? page;
            }

            return page;
        }

        private AnimeSitePage Direct(string url, string referer, AnimeSiteFetchOptions fetch)
        {
            if (fetch.UsesSession)
            {
                try
                {
                    return new AnimeSitePage { Html = _sessionClient.GetHtml(url, referer, fetch.Session), FinalUrl = url };
                }
                catch (AnimeSiteSessionExpiredException)
                {
                    return new AnimeSitePage { FinalUrl = url };
                }
            }

            try
            {
                var response = _httpClient.Get(AnimeSiteHttp.BuildRequest(url, referer));
                return new AnimeSitePage
                {
                    Html = response.Content,
                    FinalUrl = response.Request?.Url?.FullUri ?? url
                };
            }
            catch (HttpException ex)
            {
                // Keep the error body; Auto mode inspects it for challenge markers.
                return new AnimeSitePage
                {
                    Html = ex.Response?.Content ?? string.Empty,
                    FinalUrl = ex.Response?.Request?.Url?.FullUri ?? url
                };
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "AnimeSite direct fetch failed for {0}", url);
                return new AnimeSitePage { FinalUrl = url };
            }
        }

        public AnimeSitePage PostPage(string url, string postData, AnimeSiteFetchOptions fetch)
        {
            fetch ??= AnimeSiteFetchOptions.Direct;

            if (fetch.UsesHeadless)
            {
                var headless = Headless("request.post", url, postData ?? string.Empty, fetch);
                if (headless != null)
                {
                    return headless;
                }
            }

            try
            {
                var request = new HttpRequest(url) { Method = HttpMethod.Post, AllowAutoRedirect = true };
                AnimeSiteHttp.ApplyBrowserHeaders(request, null);
                request.Headers.ContentType = "application/x-www-form-urlencoded";
                request.SetContent(postData ?? string.Empty);

                var response = _httpClient.Execute(request);
                return new AnimeSitePage
                {
                    Html = response.Content,
                    FinalUrl = response.Request?.Url?.FullUri ?? url
                };
            }
            catch (HttpException ex)
            {
                return new AnimeSitePage { Html = ex.Response?.Content ?? string.Empty, FinalUrl = url };
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "AnimeSite direct POST failed for {0}", url);
                return new AnimeSitePage { FinalUrl = url };
            }
        }

        public AnimeSiteResolvedLink ResolvePage(string url, string clickText, string resultSelector, AnimeSiteFetchOptions fetch)
        {
            fetch ??= AnimeSiteFetchOptions.Direct;

            if (!fetch.UsesResolver)
            {
                return new AnimeSiteResolvedLink { Error = "No Page Resolver URL configured for this indexer." };
            }

            try
            {
                var payload = new Dictionary<string, object> { ["url"] = url, ["timeoutMs"] = 60000 };
                if (!string.IsNullOrWhiteSpace(clickText))
                {
                    payload["clickText"] = clickText;
                }

                if (!string.IsNullOrWhiteSpace(resultSelector))
                {
                    payload["resultSelector"] = resultSelector;
                }

                var request = new HttpRequest(fetch.PageResolverUrl.TrimEnd('/') + "/resolve") { Method = HttpMethod.Post };
                request.Headers.ContentType = "application/json";
                request.Headers.Accept = "application/json";
                request.SetContent(JsonSerializer.Serialize(payload));
                request.RequestTimeout = TimeSpan.FromSeconds(90);
                request.SuppressHttpError = true;

                var response = _httpClient.Execute(request);
                var result = JsonSerializer.Deserialize<AnimeSiteResolvedLink>(response.Content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result == null)
                {
                    return new AnimeSiteResolvedLink { Error = "Page resolver returned an empty response." };
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Page resolver ({0}) failed for {1}", fetch.PageResolverUrl, url);
                return new AnimeSiteResolvedLink { Error = ex.Message };
            }
        }

        private AnimeSitePage Headless(string url, AnimeSiteFetchOptions fetch)
        {
            return Headless("request.get", url, null, fetch);
        }

        private AnimeSitePage Headless(string cmd, string url, string postData, AnimeSiteFetchOptions fetch)
        {
            try
            {
                object payload = string.Equals(cmd, "request.post", StringComparison.Ordinal)
                    ? new { cmd, url, postData, maxTimeout = 60000 }
                    : new { cmd, url, maxTimeout = 60000 };

                var body = JsonSerializer.Serialize(payload);

                var request = new HttpRequest(fetch.HeadlessUrl.TrimEnd('/')) { Method = HttpMethod.Post };
                request.Headers.ContentType = "application/json";
                request.Headers.Accept = "application/json";
                request.SetContent(body);
                request.RequestTimeout = TimeSpan.FromSeconds(75);

                var response = _httpClient.Execute(request);
                var result = JsonSerializer.Deserialize<FlareSolverrResponse>(response.Content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result?.Solution?.Response == null || !string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("Headless browser ({0}) returned no solution for {1} ({2}): {3}", fetch.HeadlessUrl, url, cmd, result?.Message);
                    return null;
                }

                return new AnimeSitePage
                {
                    Html = result.Solution.Response,
                    FinalUrl = string.IsNullOrWhiteSpace(result.Solution.Url) ? url : result.Solution.Url
                };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Headless browser request to {0} failed for {1}", fetch.HeadlessUrl, url);
                return null;
            }
        }

        // Cloudflare challenge-page markers.
        private static bool LooksBlocked(string html)
        {
            if (string.IsNullOrWhiteSpace(html) || html.Length > 60000)
            {
                return false;
            }

            return html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                   || html.Contains("_cf_chl_opt", StringComparison.Ordinal)
                   || html.Contains("id=\"challenge-form\"", StringComparison.Ordinal)
                   || html.Contains("cf-mitigated", StringComparison.Ordinal)
                   || html.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase);
        }

        private class FlareSolverrResponse
        {
            public string Status { get; set; }
            public string Message { get; set; }

            [JsonPropertyName("solution")]
            public FlareSolverrSolution Solution { get; set; }
        }

        private class FlareSolverrSolution
        {
            public string Url { get; set; }
            public int Status { get; set; }
            public string Response { get; set; }
        }
    }
}
