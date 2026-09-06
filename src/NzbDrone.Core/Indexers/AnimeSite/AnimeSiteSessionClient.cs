using System;
using System.Net;
using System.Net.Http;
using NLog;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // A dedicated HttpClient with its own CookieContainer that sends the
    // configured Session User-Agent verbatim. The shared IHttpClient always
    // appends its own User-Agent, which invalidates a cf_clearance cookie.
    public interface IAnimeSiteSessionClient
    {
        // Returns the page HTML. Throws AnimeSiteSessionExpiredException on a
        // 403; returns "" on any other transport error.
        string GetHtml(string url, string referer, IndexerSessionConfig session);
    }

    public sealed class AnimeSiteSessionClient : IAnimeSiteSessionClient, IDisposable
    {
        private readonly Logger _logger;
        private readonly CookieContainer _cookies = new();
        private readonly HttpClient _client;

        public AnimeSiteSessionClient(Logger logger)
        {
            _logger = logger;

            var socketsHandler = new SocketsHttpHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(20)
            };

            // Logs the request + any failing response body at Trace level.
            var handler = new LoggingHttpHandler(logger, socketsHandler);

            _client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        public string GetHtml(string url, string referer, IndexerSessionConfig session)
        {
            if (session is not { IsConfigured: true })
            {
                throw new InvalidOperationException("AnimeSiteSessionClient invoked without a configured session.");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                _logger.Debug("AnimeSite session client: invalid url '{0}'", url);
                return string.Empty;
            }

            // Domain-scoped clearance cookie; CookieContainer de-dupes by name+domain+path.
            _cookies.Add(new Cookie("cf_clearance", session.ClearanceToken, "/", "." + session.TargetDomain));

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            request.Headers.TryAddWithoutValidation("User-Agent", session.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", referer == null ? "none" : "same-origin");

            if (!string.IsNullOrWhiteSpace(referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", referer);
            }

            HttpResponseMessage response;

            try
            {
                response = _client.Send(request);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "AnimeSite session request to {0} failed", url);
                return string.Empty;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    AnimeSiteSessionStatus.MarkExpired(session.TargetDomain);

                    _logger.Error("AnimeSite: {0} returned 403 with the configured session. The cf_clearance cookie for '{1}' has most likely expired.", url, session.TargetDomain);

                    throw new AnimeSiteSessionExpiredException(session.TargetDomain);
                }

                string body;

                try
                {
                    body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "AnimeSite session response body read failed for {0}", url);
                    return string.Empty;
                }

                if (response.IsSuccessStatusCode)
                {
                    AnimeSiteSessionStatus.MarkHealthy(session.TargetDomain);
                }
                else
                {
                    _logger.Debug("AnimeSite session request to {0} returned {1}", url, (int)response.StatusCode);
                }

                return body;
            }
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
