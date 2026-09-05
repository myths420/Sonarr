using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.AnimeSite;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv;
using Sonarr.Http;

namespace Sonarr.Api.V5.AnimeSite;

// Read-only: shows are populated by SiteShowSyncCommand (POST
// /api/v5/command {"name":"SiteShowSync","sourceListId":N}), triggered
// from the Sites catalogue page's Refresh action.
[V5ApiController("siteshow")]
public class SiteShowController : Controller
{
    private readonly ISiteShowService _siteShowService;
    private readonly ISiteDownloadService _siteDownloadService;
    private readonly ISiteShowPosterService _posterService;
    private readonly ISeriesService _seriesService;

    public SiteShowController(ISiteShowService siteShowService,
                             ISiteDownloadService siteDownloadService,
                             ISiteShowPosterService posterService,
                             ISeriesService seriesService)
    {
        _siteShowService = siteShowService;
        _siteDownloadService = siteDownloadService;
        _posterService = posterService;
        _seriesService = seriesService;
    }

    [HttpGet]
    [Produces("application/json")]
    public List<SiteShowResource> GetSiteShows([FromQuery] int sourceListId)
    {
        var resources = _siteShowService.GetForSourceList(sourceListId).ToResource();
        LinkLibrarySeries(resources);
        return resources;
    }

    [HttpGet("{id:int}")]
    [Produces("application/json")]
    public ActionResult<SiteShowResource> GetSiteShow(int id)
    {
        var show = _siteShowService.Get(id);

        if (show == null)
        {
            return NotFound();
        }

        var resource = show.ToResource()!;
        LinkLibrarySeries(new List<SiteShowResource> { resource });
        return resource;
    }

    // Cross-reference the catalogue against the Sonarr library by cleaned
    // title -- these shows aren't TVDB-keyed, so a normalised title match is
    // the only link we have. Cheap: one GetAllSeries() (already cached) and a
    // dictionary lookup per row.
    private void LinkLibrarySeries(List<SiteShowResource> resources)
    {
        if (resources.Count == 0)
        {
            return;
        }

        var seriesByCleanTitle = new Dictionary<string, NzbDrone.Core.Tv.Series>();
        foreach (var series in _seriesService.GetAllSeries())
        {
            var clean = series.Title.CleanSeriesTitle();
            if (!string.IsNullOrEmpty(clean))
            {
                seriesByCleanTitle.TryAdd(clean, series);
            }
        }

        foreach (var resource in resources)
        {
            var clean = (resource.Title ?? string.Empty).CleanSeriesTitle();
            if (!string.IsNullOrEmpty(clean) && seriesByCleanTitle.TryGetValue(clean, out var series))
            {
                resource.SeriesId = series.Id;
                resource.SeriesTitleSlug = series.TitleSlug;
            }
        }
    }

    // Locally-cached poster. Downloaded from AniList once (on metadata
    // backfill, or lazily here on first request) and served from disk after
    // that -- browsing the catalogue doesn't re-hit AniList's CDN.
    [HttpGet("{id:int}/poster")]
    public IActionResult GetSiteShowPoster(int id)
    {
        var show = _siteShowService.Get(id);
        if (show == null)
        {
            return NotFound();
        }

        var path = _posterService.GetPosterPath(show);
        if (path == null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=2592000";

        return PhysicalFile(path, "image/jpeg");
    }

    [HttpGet("{id:int}/episodes")]
    [Produces("application/json")]
    public List<SiteShowEpisodeResource> GetSiteShowEpisodes(int id)
    {
        return _siteShowService.GetEpisodes(id).ToResource();
    }

    [HttpGet("{id:int}/episodes/{number:int}/releases")]
    [Produces("application/json")]
    public List<SiteShowReleaseResource> GetSiteShowEpisodeReleases(int id, int number)
    {
        return _siteShowService.ResolveEpisodeReleases(id, number).ToResource();
    }

    // Starts a download for one episode. With no releaseUrl the top-ranked
    // release is picked automatically (English-preferred, highest quality,
    // most reliable host); pass releaseUrl to download a specific one the
    // user chose from the Search results.
    [HttpPost("{id:int}/episodes/{number:int}/download")]
    [Produces("application/json")]
    public ActionResult<SiteDownloadResource> DownloadSiteShowEpisode(int id, int number, [FromQuery] string? releaseUrl = null)
    {
        var download = _siteDownloadService.StartDownload(id, number, releaseUrl);

        if (download == null)
        {
            return UnprocessableEntity("No downloadable release could be resolved for this episode.");
        }

        return download.ToResource();
    }
}
