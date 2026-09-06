using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // Adds the request headers a desktop Chrome sends, so a site that
    // filters non-browser clients serves the normal page. Only gets past
    // passive header checks, not an active Cloudflare challenge.
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
