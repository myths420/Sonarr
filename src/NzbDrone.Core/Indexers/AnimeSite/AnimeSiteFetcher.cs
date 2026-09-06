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

    // How AnimeSite page fetches should be done for one site. Built from
    // AnimeSiteSettings and carried alongside the other *Options types so
    // every fetch point (catalogue browse, episode/landing pages, the
    // scraping script's host.get) can honour it.
    public class AnimeSiteFetchOptions
    {
        public string HeadlessUrl { get; set; }
        public AnimeSiteBrowserMode Mode { get; set; }

        public static readonly AnimeSiteFetchOptions Direct = new AnimeSiteFetchOptions();

        public bool UsesHeadless => Mode != AnimeSiteBrowserMode.Off && !string.IsNullOrWhiteSpace(HeadlessUrl);

        public static AnimeSiteFetchOptions FromSettings(AnimeSiteSettings settings)
        {
            return new AnimeSiteFetchOptions
            {
                HeadlessUrl = settings.HeadlessBrowserUrl,
                Mode = (AnimeSiteBrowserMode)settings.HeadlessBrowserMode
            };
        }
    }

    // Fetches a page's HTML, optionally through a headless browser
    // (FlareSolverr) so Cloudflare's "Just a moment" interstitial and other
    // JS-gated pages can be read. FlareSolverr can't solve a Turnstile
    // captcha (e.g. vikingfile's download gate) -- that still needs a human.
    public interface IAnimeSiteFetcher
    {
        string GetHtml(string url, string referer, AnimeSiteFetchOptions fetch);
    }

    public class AnimeSiteFetcher : IAnimeSiteFetcher
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public AnimeSiteFetcher(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public string GetHtml(string url, string referer, AnimeSiteFetchOptions fetch)
        {
            fetch ??= AnimeSiteFetchOptions.Direct;

            if (fetch.UsesHeadless && fetch.Mode == AnimeSiteBrowserMode.Always)
            {
                return Headless(url, fetch) ?? Direct(url, referer);
            }

            var html = Direct(url, referer);

            if (fetch.UsesHeadless && fetch.Mode == AnimeSiteBrowserMode.Auto && LooksBlocked(html))
            {
                _logger.Debug("AnimeSite: {0} looks Cloudflare-blocked, retrying via headless browser", url);
                return Headless(url, fetch) ?? html;
            }

            return html;
        }

        private string Direct(string url, string referer)
        {
            try
            {
                return _httpClient.Get(AnimeSiteHttp.BuildRequest(url, referer)).Content;
            }
            catch (HttpException ex)
            {
                // A 403/503 body is still useful -- Auto mode inspects it to
                // decide whether to fall back to the headless browser.
                return ex.Response?.Content ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "AnimeSite direct fetch failed for {0}", url);
                return string.Empty;
            }
        }

        private string Headless(string url, AnimeSiteFetchOptions fetch)
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

                return result.Solution.Response;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Headless browser request to {0} failed for {1}", fetch.HeadlessUrl, url);
                return null;
            }
        }

        // Markers a Cloudflare IUAM / challenge page carries but a real page
        // doesn't. Kept deliberately narrow to avoid false positives on
        // pages that merely mention Cloudflare.
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
