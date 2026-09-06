using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.AnimeSite;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv;
using Sonarr.Http;

namespace Sonarr.Api.V5.AnimeSite;

// Catalogue rows are populated by SiteShowSyncCommand.
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

    // Sets SeriesId / SeriesTitleSlug on each row when a library series
    // matches by site-show id, AniList id, or cleaned title.
    private void LinkLibrarySeries(List<SiteShowResource> resources)
    {
        if (resources.Count == 0)
        {
            return;
        }

        var seriesByCleanTitle = new Dictionary<string, NzbDrone.Core.Tv.Series>();
        var seriesByAniListId = new Dictionary<int, NzbDrone.Core.Tv.Series>();
        var seriesBySiteShowId = new Dictionary<int, NzbDrone.Core.Tv.Series>();
        foreach (var series in _seriesService.GetAllSeries())
        {
            var clean = series.Title.CleanSeriesTitle();
            if (!string.IsNullOrEmpty(clean))
            {
                seriesByCleanTitle.TryAdd(clean, series);
            }

            foreach (var aniListId in series.AniListIds)
            {
                seriesByAniListId.TryAdd(aniListId, series);
            }

            if (SiteSeriesIds.IsSiteId(series.TvdbId))
            {
                seriesBySiteShowId.TryAdd(SiteSeriesIds.ToSiteShowId(series.TvdbId), series);
            }
        }

        foreach (var resource in resources)
        {
            NzbDrone.Core.Tv.Series? series = null;

            // Exact id links first, cleaned title as a fallback.
            if (seriesBySiteShowId.TryGetValue(resource.Id, out var siteSeries))
            {
                series = siteSeries;
            }

            if (series == null && resource.AniListId > 0)
            {
                seriesByAniListId.TryGetValue(resource.AniListId, out series);
            }

            if (series == null)
            {
                var clean = (resource.Title ?? string.Empty).CleanSeriesTitle();
                if (!string.IsNullOrEmpty(clean))
                {
                    seriesByCleanTitle.TryGetValue(clean, out series);
                }
            }

            if (series != null)
            {
                resource.SeriesId = series.Id;
                resource.SeriesTitleSlug = series.TitleSlug;
            }
        }
    }

    // Locally-cached poster, served from disk.
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

    // Downloads one episode: the top-ranked release, or ?releaseUrl=.
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

    // Adds this catalogue show to the Series tab. Returns the re-linked row.
    [HttpPost("{id:int}/add")]
    [Produces("application/json")]
    public ActionResult<SiteShowResource> AddSiteShowAsSeries(int id, [FromBody] SiteShowAddResource? request)
    {
        var show = _siteShowService.Get(id);
        if (show == null)
        {
            return NotFound();
        }

        try
        {
            _siteShowService.AddAsSeries(
                id,
                request?.RootFolderPath,
                request?.QualityProfileId,
                request?.SearchForMissingEpisodes ?? false);
        }
        catch (SiteSeriesAddException ex)
        {
            return UnprocessableEntity(ex.Message);
        }

        var resource = _siteShowService.Get(id).ToResource()!;
        LinkLibrarySeries(new List<SiteShowResource> { resource });
        return resource;
    }
}
