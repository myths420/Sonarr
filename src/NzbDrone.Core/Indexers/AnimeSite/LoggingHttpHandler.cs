using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // DelegatingHandler that logs the outbound request at Trace level and
    // the response body on any non-success status.
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

            // A failing body is logged regardless of level (it carries the
            // validation detail and is small).
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
                // Buffer so the body can be logged and still sent.
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
                // ReadAsStringAsync buffers, so the caller can still read it after.
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
