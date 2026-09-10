using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.AnimeSite;
using NzbDrone.Core.Parser;
using NzbDrone.Core.SeriesStats;
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
    private readonly ISeriesStatisticsService _seriesStatisticsService;
    private readonly IEpisodeService _episodeService;

    public SiteShowController(ISiteShowService siteShowService,
                             ISiteDownloadService siteDownloadService,
                             ISiteShowPosterService posterService,
                             ISeriesService seriesService,
                             ISeriesStatisticsService seriesStatisticsService,
                             IEpisodeService episodeService)
    {
        _siteShowService = siteShowService;
        _siteDownloadService = siteDownloadService;
        _posterService = posterService;
        _seriesService = seriesService;
        _seriesStatisticsService = seriesStatisticsService;
        _episodeService = episodeService;
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

        var statsBySeriesId = _seriesStatisticsService.SeriesStatistics().ToDictionary(s => s.SeriesId);

        var seriesByCleanTitle = new Dictionary<string, NzbDrone.Core.Tv.Series>();
        var seriesByAniListId = new Dictionary<int, NzbDrone.Core.Tv.Series>();
        var seriesBySiteShowId = new Dictionary<int, NzbDrone.Core.Tv.Series>();
        var seriesById = new Dictionary<int, NzbDrone.Core.Tv.Series>();
        foreach (var series in _seriesService.GetAllSeries())
        {
            seriesById[series.Id] = series;

            var clean = series.Title.CleanSeriesTitle();
            if (!string.IsNullOrEmpty(clean))
            {
                seriesByCleanTitle.TryAdd(clean, series);
            }

            // Also index the season-suffix-stripped title so a "Season 2"
            // catalogue row links to the series it was folded into, and the
            // cross-site key (year / "New" / sub tags stripped too).
            var baseClean = SeasonTitleParser.Parse(series.Title).BaseTitle.CleanSeriesTitle();
            if (!string.IsNullOrEmpty(baseClean))
            {
                seriesByCleanTitle.TryAdd(baseClean, series);
            }

            var matchKey = SiteTitleMatch.Key(series.Title);
            if (!string.IsNullOrEmpty(matchKey))
            {
                seriesByCleanTitle.TryAdd(matchKey, series);
            }

            // AniList ids: the one in the synthetic tvdb id, plus folded ones.
            foreach (var aniListId in SiteTitleMatch.AniListIds(series))
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

            // A hand-set link wins over everything.
            if (resource.MappedSeriesId > 0)
            {
                seriesById.TryGetValue(resource.MappedSeriesId, out series);
            }

            // Exact id links next, cleaned title as a fallback.
            if (series == null && seriesBySiteShowId.TryGetValue(resource.Id, out var siteSeries))
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

            if (series == null)
            {
                var matchKey = SiteTitleMatch.Key(resource.Title);
                if (!string.IsNullOrEmpty(matchKey))
                {
                    seriesByCleanTitle.TryGetValue(matchKey, out series);
                }
            }

            if (series != null)
            {
                resource.SeriesId = series.Id;
                resource.SeriesTitleSlug = series.TitleSlug;

                if (statsBySeriesId.TryGetValue(series.Id, out var stats))
                {
                    resource.SeriesEpisodeFileCount = stats.EpisodeFileCount;
                    resource.SeriesEpisodeCount = stats.EpisodeCount;
                }
            }
        }
    }

    // Locally-cached poster, served from disk. HEAD is allowed too --
    // Sonarr's MediaCoverService probes with HEAD before downloading.
    [HttpGet("{id:int}/poster")]
    [HttpHead("{id:int}/poster")]
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
        var resources = _siteShowService.GetEpisodes(id).ToResource();

        var show = _siteShowService.Get(id);
        if (show == null)
        {
            return resources;
        }

        // Reuse the series-linking logic, then flag episodes whose file is
        // already on disk in the linked series.
        var linked = show.ToResource()!;
        LinkLibrarySeries(new List<SiteShowResource> { linked });

        if (linked.SeriesId is int seriesId)
        {
            var episodes = _episodeService.GetEpisodeBySeries(seriesId);
            var season = show.MappedSeason > 0
                ? show.MappedSeason
                : SeasonTitleParser.Parse(show.Title).Season;

            // A scrape-backed series keeps every episode in season 1 even
            // when the catalogue row says "Season 5". If the linked series
            // has no episodes in the row's season, match on episode number
            // alone.
            var hasThatSeason = episodes.Any(e => e.SeasonNumber == season);

            var onDisk = episodes
                .Where(e => e.HasFile && (!hasThatSeason || e.SeasonNumber == season))
                .Select(e => e.EpisodeNumber)
                .ToHashSet();

            foreach (var resource in resources)
            {
                resource.HasFile = onDisk.Contains(resource.Number);
            }
        }

        return resources;
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

    // Pins this catalogue row to a library series by hand (seriesId 0 or
    // omitted clears it). season defaults to the one parsed from the title.
    [HttpPut("{id:int}/link")]
    [Produces("application/json")]
    public ActionResult<SiteShowResource> SetSiteShowLink(int id, [FromBody] SiteShowLinkResource? request)
    {
        if (_siteShowService.Get(id) == null)
        {
            return NotFound();
        }

        try
        {
            _siteShowService.SetManualLink(id, request?.SeriesId ?? 0, request?.Season);
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
