using System;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.AnimeSite
{
    public interface ISiteShowPosterService
    {
        // Local path of the cached poster jpg for this show, downloading it
        // from the metadata provider's URL on first request. Returns null if
        // there's nothing to cache or the download failed -- caller should
        // then just show a placeholder.
        string GetPosterPath(SiteShow show);

        // Fire this after metadata backfill so the poster is already on disk
        // by the time the catalogue is browsed.
        void PreCache(SiteShow show);
    }

    public class SiteShowPosterService : ISiteShowPosterService
    {
        private readonly IHttpClient _httpClient;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;
        private readonly string _cacheDir;

        public SiteShowPosterService(IHttpClient httpClient,
                                     IDiskProvider diskProvider,
                                     IAppFolderInfo appFolderInfo,
                                     Logger logger)
        {
            _httpClient = httpClient;
            _diskProvider = diskProvider;
            _logger = logger;
            _cacheDir = Path.Combine(appFolderInfo.AppDataFolder, "sitecovers");
        }

        public string GetPosterPath(SiteShow show)
        {
            if (show == null || string.IsNullOrWhiteSpace(show.PosterUrl))
            {
                return null;
            }

            // Key on AniList id where we have one (stable, shared across
            // sites and re-syncs); otherwise fall back to the show id.
            var key = show.AniListId > 0 ? "al" + show.AniListId : "sh" + show.Id;
            var path = Path.Combine(_cacheDir, key + ".jpg");

            if (_diskProvider.FileExists(path))
            {
                return path;
            }

            try
            {
                _diskProvider.EnsureFolder(_cacheDir);
                Download(show.PosterUrl, path);
                return _diskProvider.FileExists(path) ? path : null;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to cache poster for '{0}' from {1}", show.Title, show.PosterUrl);
                return null;
            }
        }

        public void PreCache(SiteShow show)
        {
            GetPosterPath(show);
        }

        private void Download(string url, string path)
        {
            var partPath = path + ".part";

            // Site-hosted posters (WordPress /wp-content/uploads/...) 403 a
            // request with no Referer -- Cloudflare hotlink protection. Send
            // the site's own origin as the Referer, same as a browser
            // loading the image from a page on that site would.
            var referer = url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                referer = uri.GetLeftPart(UriPartial.Authority) + "/";
            }

            try
            {
                using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite))
                {
                    var request = new HttpRequest(url)
                    {
                        AllowAutoRedirect = true,
                        ResponseStream = fileStream
                    };
                    request.Headers.Add("Referer", referer);

                    var response = _httpClient.GetAsync(request).GetAwaiter().GetResult();

                    if (response.Headers.ContentType != null &&
                        (response.Headers.ContentType.Contains("text/html") ||
                         response.Headers.ContentType.Contains("application/json")))
                    {
                        throw new HttpException(request, response, "Poster host returned a page, not an image.");
                    }
                }

                if (_diskProvider.FileExists(path))
                {
                    _diskProvider.DeleteFile(path);
                }

                _diskProvider.MoveFile(partPath, path);
            }
            finally
            {
                if (_diskProvider.FileExists(partPath))
                {
                    _diskProvider.DeleteFile(partPath);
                }
            }
        }
    }
}
