using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.AnimeSite;
using Sonarr.Http;

namespace Sonarr.Api.V5.AnimeSite;

// Lets a browser extension keep an AnimeSite indexer's manually-synced
// Cloudflare session (cf_clearance cookie + User-Agent) fresh without the
// user re-pasting it by hand every time it expires.
//
// Auth: every /api/v5/* route is API-key protected by the host pipeline
// (same as the rest of Sonarr's API) -- an extension must send the
// instance's API key (X-Api-Key header or ?apikey=). No extra check is
// added here; adding one would only diverge from how the rest of the API
// behaves.
[V5ApiController("indexer/session")]
public class IndexerSessionController : Controller
{
    private const string AnimeSiteImplementation = "AnimeSiteIndexer";

    private readonly IIndexerFactory _indexerFactory;
    private readonly Logger _logger;

    public IndexerSessionController(IIndexerFactory indexerFactory, Logger logger)
    {
        _indexerFactory = indexerFactory;
        _logger = logger;
    }

    // GET /api/v5/indexer/session
    // One row per AnimeSite indexer -- whether it has a session configured
    // and whether that session has been failing (403). Lets the extension
    // decide when it actually needs to push a fresh token.
    [HttpGet]
    [Produces("application/json")]
    public List<IndexerSessionStatusResource> GetSessions()
    {
        var expired = AnimeSiteSessionStatus.ExpiredDomains
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return AnimeSiteIndexers()
            .Select(d =>
            {
                var settings = (AnimeSiteSettings)d.Settings;
                var host = HostOf(settings.BaseUrl);

                return new IndexerSessionStatusResource
                {
                    IndexerId = d.Id,
                    IndexerName = d.Name,
                    TargetDomain = host,
                    HasSession = !string.IsNullOrWhiteSpace(settings.SessionClearanceCookie) &&
                                 !string.IsNullOrWhiteSpace(settings.SessionUserAgent),
                    Expired = host != null && expired.Contains(host)
                };
            })
            .ToList();
    }

    // POST /api/v5/indexer/session
    // { "targetDomain": "animexin.dev", "tokenValue": "cf_clearance=...", "userAgent": "Mozilla/5.0 ..." }
    [HttpPost]
    [Produces("application/json")]
    public ActionResult<IndexerSessionUpdateResultResource> UpdateSession([FromBody] UpdateSessionRequest? request)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.TargetDomain) ||
            string.IsNullOrWhiteSpace(request.TokenValue) ||
            string.IsNullOrWhiteSpace(request.UserAgent))
        {
            return BadRequest("targetDomain, tokenValue and userAgent are all required.");
        }

        var domain = HostOf(request.TargetDomain);
        if (domain == null)
        {
            return BadRequest($"'{request.TargetDomain}' is not a valid domain.");
        }

        var matches = AnimeSiteIndexers()
            .Where(d => DomainMatches(HostOf(((AnimeSiteSettings)d.Settings).BaseUrl), domain))
            .ToList();

        if (matches.Count == 0)
        {
            return NotFound($"No AnimeSite indexer is configured for '{domain}'.");
        }

        var token = request.TokenValue.Trim();
        var userAgent = request.UserAgent.Trim();

        foreach (var definition in matches)
        {
            var settings = (AnimeSiteSettings)definition.Settings;
            settings.SessionClearanceCookie = token;
            settings.SessionUserAgent = userAgent;

            // Persists to the Indexers table and raises
            // ProviderUpdatedEvent<IIndexer>. AnimeSiteFetchOptions is
            // rebuilt from settings on every fetch and AnimeSiteSessionClient
            // replaces the cookie by name on its next request, so there's no
            // stale client state to flush -- the next fetch uses the new
            // session. Clearing the health-check flag here just makes the UI
            // reflect it immediately rather than on the next success.
            _indexerFactory.Update(definition);
        }

        AnimeSiteSessionStatus.MarkHealthy(domain);

        var names = matches.Select(m => m.Name).ToList();
        _logger.Info("Updated Cloudflare session for {0} indexer(s) matching '{1}': {2}", matches.Count, domain, string.Join(", ", names));

        return new IndexerSessionUpdateResultResource
        {
            TargetDomain = domain,
            UpdatedIndexers = names
        };
    }

    private List<IndexerDefinition> AnimeSiteIndexers()
    {
        return _indexerFactory.All()
            .Where(d => d.Implementation == AnimeSiteImplementation && d.Settings is AnimeSiteSettings)
            .ToList();
    }

    private static bool DomainMatches(string? a, string? b)
    {
        if (a == null || b == null)
        {
            return false;
        }

        a = a.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? a[4..] : a;
        b = b.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? b[4..] : b;

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // Accepts "animexin.dev", "https://animexin.dev/", "http://www.animexin.dev/anime/".
    private static string? HostOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Host.ToLowerInvariant();
        }

        if (Uri.TryCreate("https://" + value, UriKind.Absolute, out uri))
        {
            return uri.Host.ToLowerInvariant();
        }

        return null;
    }
}
