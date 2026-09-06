using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Indexers.AnimeSite
{
    // The Session Clearance Cookie + Session User-Agent settings, resolved
    // for one site. Cloudflare binds cf_clearance to the User-Agent, so both
    // are required together.
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

        // Accepts the bare token or a full "cf_clearance=...; __cf_bm=..." string.
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

    // Thrown when a request carrying a configured session returns 403.
    public class AnimeSiteSessionExpiredException : Exception
    {
        public string Domain { get; }

        public AnimeSiteSessionExpiredException(string domain)
            : base($"The session for '{domain}' was rejected (403). The cf_clearance cookie has most likely expired; copy a fresh cookie and matching User-Agent from your browser into the indexer.")
        {
            Domain = domain;
        }
    }

    // Sites whose configured session last returned 403, for the health
    // check. Cleared when a request with that session next succeeds.
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
