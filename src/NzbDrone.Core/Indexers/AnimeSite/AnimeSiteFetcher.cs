using System;
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

        public static readonly AnimeSiteFetchOptions Direct = new AnimeSiteFetchOptions();

        public bool UsesHeadless => Mode != AnimeSiteBrowserMode.Off && !string.IsNullOrWhiteSpace(HeadlessUrl);

        public bool UsesSession => Session is { IsConfigured: true };

        public static AnimeSiteFetchOptions FromSettings(AnimeSiteSettings settings)
        {
            return new AnimeSiteFetchOptions
            {
                HeadlessUrl = settings.HeadlessBrowserUrl,
                Mode = (AnimeSiteBrowserMode)settings.HeadlessBrowserMode,
                Session = IndexerSessionConfig.FromSettings(settings)
            };
        }
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

            if (fetch.UsesHeadless && fetch.Mode == AnimeSiteBrowserMode.Auto && LooksBlocked(page.Html))
            {
                _logger.Debug("AnimeSite: {0} looks Cloudflare-blocked, retrying via headless browser", url);
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

        private AnimeSitePage Headless(string url, AnimeSiteFetchOptions fetch)
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    cmd = "request.get",
                    url,
                    maxTimeout = 60000
                });

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
                    _logger.Warn("Headless browser ({0}) returned no solution for {1}: {2}", fetch.HeadlessUrl, url, result?.Message);
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
