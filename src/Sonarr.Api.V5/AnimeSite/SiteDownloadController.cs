using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.AnimeSite;
using Sonarr.Http;

namespace Sonarr.Api.V5.AnimeSite;

// In-memory download tracker for the Sites catalogue -- separate from
// Sonarr's own queue/history since these downloads have no Series/Episode
// library entry behind them. See SiteDownloadService.
[V5ApiController("sitedownload")]
public class SiteDownloadController : Controller
{
    private readonly ISiteDownloadService _siteDownloadService;

    public SiteDownloadController(ISiteDownloadService siteDownloadService)
    {
        _siteDownloadService = siteDownloadService;
    }

    [HttpGet]
    [Produces("application/json")]
    public List<SiteDownloadResource> GetSiteDownloads()
    {
        return _siteDownloadService.GetDownloads().ToResource();
    }

    [HttpDelete("{downloadId}")]
    public ActionResult CancelSiteDownload(string downloadId)
    {
        return _siteDownloadService.CancelDownload(downloadId) ? Ok() : NotFound();
    }
}
