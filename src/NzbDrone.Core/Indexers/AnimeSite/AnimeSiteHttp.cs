using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // Scraper sites routinely serve a different page -- or a 403 -- to a
    // client whose headers don't look like a browser's. Add the headers a
    // desktop Chrome sends (same idea as the request headers the Python
    // scraper this fork is based on used).
    //
    // Scope note: this only gets past passive header filtering. It does NOT
    // solve an active Cloudflare "Just a moment" (IUAM) interstitial or a
    // Turnstile challenge -- those need a real browser running the challenge
    // JS (e.g. a FlareSolverr instance for IUAM; Turnstile isn't solvable by
    // FlareSolverr either). Sonarr's HTTP layer also always prepends its own
    // User-Agent, so this deliberately doesn't fight that.
    public static class AnimeSiteHttp
    {
        public static HttpRequest BuildRequest(string url, string referer = null)
        {
            var request = new HttpRequest(url) { AllowAutoRedirect = true };
            ApplyBrowserHeaders(request, referer);
            return request;
        }

        public static void ApplyBrowserHeaders(HttpRequest request, string referer = null)
        {
            request.Headers.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8";
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Upgrade-Insecure-Requests", "1");
            request.Headers.Add("Sec-Fetch-Dest", "document");
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Add("Sec-Fetch-Site", referer == null ? "none" : "same-origin");

            if (!string.IsNullOrWhiteSpace(referer))
            {
                request.Headers.Add("Referer", referer);
            }
        }
    }
}
