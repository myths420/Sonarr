using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // A manually-synchronised browser session for a Cloudflare-gated site.
    // The user clears the challenge once in a real browser, then pastes the
    // cf_clearance cookie it produced plus the exact User-Agent that earned
    // it. Cloudflare binds cf_clearance to the User-Agent (and client IP),
    // so the two must travel together and match the browser they came from
    // -- that's why the User-Agent is a paired field, not a nicety.
    //
    // This is the same idea as an indexer "Cookie" field in Prowlarr /
    // Jackett: no challenge is solved here, an already-solved session is
    // just carried on outbound requests until it expires.
    public class IndexerSessionConfig
    {
        public string TargetDomain { get; set; }
        public string ClearanceToken { get; set; }
        public string UserAgent { get; set; }

        public bool IsConfigured =>
            ClearanceToken.IsNotNullOrWhiteSpace() &&
            UserAgent.IsNotNullOrWhiteSpace() &&
            TargetDomain.IsNotNullOrWhiteSpace();

        public static IndexerSessionConfig FromSettings(AnimeSiteSettings settings)
        {
            string domain = null;
            if (Uri.TryCreate(settings?.BaseUrl, UriKind.Absolute, out var uri))
            {
                domain = uri.Host;
            }

            return new IndexerSessionConfig
            {
                TargetDomain = domain,
                ClearanceToken = ExtractClearance(settings?.SessionClearanceCookie),
                UserAgent = settings?.SessionUserAgent?.Trim()
            };
        }

        // Accept either the bare token or a whole pasted cookie string
        // ("cf_clearance=abc123; __cf_bm=...; ...") -- take just cf_clearance.
        private static string ExtractClearance(string raw)
        {
            if (raw.IsNullOrWhiteSpace())
            {
                return null;
            }

            raw = raw.Trim();
            var match = Regex.Match(raw, @"cf_clearance=([^;\s]+)");
            return match.Success ? match.Groups[1].Value : raw;
        }
    }

    // Raised when a request carrying a pasted session comes back 403 --
    // almost always an expired cf_clearance. Callers swallow it (returning
    // no HTML) so a sync/search degrades to "0 results" rather than an
    // unhandled failure; AnimeSiteSessionStatus + the health check are what
    // actually tell the user to re-paste.
    public class AnimeSiteSessionExpiredException : Exception
    {
        public string Domain { get; }

        public AnimeSiteSessionExpiredException(string domain)
            : base($"The imported browser session for '{domain}' was rejected (403 Forbidden) -- its cf_clearance cookie has most likely expired. Open the site in your browser, copy a fresh cf_clearance cookie and the matching User-Agent, and update the indexer.")
        {
            Domain = domain;
        }
    }

    // Process-wide record of which sites last rejected their pasted session,
    // so a health check can surface an actionable warning in Sonarr's UI.
    // Entries are cleared as soon as a request with that session succeeds.
    public static class AnimeSiteSessionStatus
    {
        private static readonly ConcurrentDictionary<string, DateTime> Expired =
            new(StringComparer.OrdinalIgnoreCase);

        public static void MarkExpired(string domain)
        {
            if (domain.IsNotNullOrWhiteSpace())
            {
                Expired[domain] = DateTime.UtcNow;
            }
        }

        public static void MarkHealthy(string domain)
        {
            if (domain.IsNotNullOrWhiteSpace())
            {
                Expired.TryRemove(domain, out _);
            }
        }

        public static IReadOnlyCollection<string> ExpiredDomains =>
            Expired.Where(kv => kv.Value.After(DateTime.UtcNow.AddDays(-7)))
                   .Select(kv => kv.Key)
                   .OrderBy(d => d)
                   .ToList();
    }
}
