using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // Drop-in DelegatingHandler that dumps the exact bytes going out and,
    // on any non-success status, the full response body -- the place APIs
    // put their "field X is required / invalid" detail for a 400/422.
    //
    // No-op unless the logger is at Trace level, so it can sit in the
    // handler chain permanently at zero cost. Set the "AnimeSite" NLog
    // rule to Trace (Settings > General > Log Level = Trace, or the
    // logger-specific rule) to turn it on.
    public class LoggingHttpHandler : DelegatingHandler
    {
        private const int MaxBodyChars = 16000;

        private readonly Logger _logger;

        public LoggingHttpHandler(Logger logger, HttpMessageHandler inner)
            : base(inner)
        {
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var trace = _logger.IsTraceEnabled;

            if (trace)
            {
                await LogRequestAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Always surface a failing body (not just at Trace) -- a 422's
            // validation payload is the one thing you actually need, and it
            // is small.
            if (!response.IsSuccessStatusCode || trace)
            {
                await LogResponseAsync(request, response, cancellationToken).ConfigureAwait(false);
            }

            return response;
        }

        private async Task LogRequestAsync(HttpRequestMessage request, CancellationToken token)
        {
            var contentType = request.Content?.Headers?.ContentType?.ToString() ?? "(no body)";
            string body = null;

            if (request.Content != null)
            {
                // Buffer so the body can be both logged and still sent.
                await request.Content.LoadIntoBufferAsync(token).ConfigureAwait(false);
                body = await ReadSafeAsync(request.Content, token).ConfigureAwait(false);
            }

            _logger.Trace(
                "AnimeSite HTTP >>> {0} {1}\n  Content-Type: {2}\n  Body: {3}",
                request.Method,
                request.RequestUri,
                contentType,
                Truncate(body) ?? "(empty)");
        }

        private async Task LogResponseAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken token)
        {
            string body;

            try
            {
                // ReadAsStringAsync buffers the content, so reading it here
                // does not stop the caller reading it again afterwards.
                body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                body = $"(could not read response body: {ex.Message})";
            }

            var level = response.IsSuccessStatusCode ? LogLevel.Trace : LogLevel.Warn;

            _logger.Log(level,
                "AnimeSite HTTP <<< {0} {1} -> {2} {3}\n  Body: {4}",
                request.Method,
                request.RequestUri,
                (int)response.StatusCode,
                response.ReasonPhrase,
                Truncate(body) ?? "(empty)");
        }

        private static async Task<string> ReadSafeAsync(HttpContent content, CancellationToken token)
        {
            try
            {
                return await content.ReadAsStringAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return $"(could not read request body: {ex.Message})";
            }
        }

        private static string Truncate(string value)
        {
            if (value == null)
            {
                return null;
            }

            return value.Length <= MaxBodyChars
                ? value
                : value[..MaxBodyChars] + $"... [+{value.Length - MaxBodyChars} chars]";
        }
    }
}
