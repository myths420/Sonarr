using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.ImportLists;

namespace NzbDrone.Core.AnimeSite
{
    public interface ISiteDownloadService
    {
        // Resolves releases for the given episode (same ranking as
        // ISiteShowService.ResolveEpisodeReleases -- English-preferred,
        // highest quality first) and starts downloading the top pick.
        // Returns null if no release could be resolved at all. Pass
        // releaseUrl to download a specific resolved release rather than the
        // top-ranked pick.
        SiteDownload StartDownload(int showId, int episodeNumber, string releaseUrl = null);

        List<SiteDownload> GetDownloads();

        bool CancelDownload(string downloadId);
    }

    public class SiteDownloadService : ISiteDownloadService
    {
        // Static, like DirectHttpDownloadClient's own _items -- both are
        // in-memory-only download trackers, and there is exactly one of
        // each kind of tracker per running Sonarr process regardless of how
        // many times this service gets constructed by DI.
        private static readonly ConcurrentDictionary<string, SiteDownload> _downloads = new();

        private readonly ISiteShowService _siteShowService;
        private readonly ISiteShowRepository _siteShowRepository;
        private readonly IImportListFactory _importListFactory;
        private readonly IHttpClient _httpClient;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public SiteDownloadService(ISiteShowService siteShowService,
                                   ISiteShowRepository siteShowRepository,
                                   IImportListFactory importListFactory,
                                   IHttpClient httpClient,
                                   IDiskProvider diskProvider,
                                   Logger logger)
        {
            _siteShowService = siteShowService;
            _siteShowRepository = siteShowRepository;
            _importListFactory = importListFactory;
            _httpClient = httpClient;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public SiteDownload StartDownload(int showId, int episodeNumber, string releaseUrl = null)
        {
            var releases = _siteShowService.ResolveEpisodeReleases(showId, episodeNumber);
            var release = string.IsNullOrWhiteSpace(releaseUrl)
                ? releases.FirstOrDefault()
                : releases.FirstOrDefault(r => string.Equals(r.Url, releaseUrl, StringComparison.OrdinalIgnoreCase)) ?? releases.FirstOrDefault();
            if (release == null)
            {
                _logger.Warn("No downloadable release resolved for show {0} episode {1}", showId, episodeNumber);
                return null;
            }

            var show = _siteShowRepository.Get(showId);
            var listDefinition = _importListFactory.Get(show.SourceListId);
            var destinationRoot = string.IsNullOrWhiteSpace(listDefinition.RootFolderPath)
                ? Path.GetTempPath()
                : listDefinition.RootFolderPath;

            var safeShowTitle = string.Join("_", show.Title.Split(Path.GetInvalidFileNameChars()));
            var safeFileName = string.Join("_", $"{show.Title} - Episode {episodeNumber:000}".Split(Path.GetInvalidFileNameChars()));
            var outputPath = Path.Combine(destinationRoot, safeShowTitle, safeFileName + ".mp4");

            var download = new SiteDownload
            {
                DownloadId = Guid.NewGuid().ToString(),
                ShowId = showId,
                EpisodeNumber = episodeNumber,
                Title = release.Title,
                OutputPath = outputPath,
                StartedAt = DateTime.UtcNow,
                Status = SiteDownloadStatus.Downloading,
                Cts = new CancellationTokenSource()
            };

            _downloads[download.DownloadId] = download;

            _ = Task.Run(() => RunDownloadAsync(download, release.Url, download.Cts.Token));

            return download;
        }

        public List<SiteDownload> GetDownloads()
        {
            return _downloads.Values.OrderByDescending(d => d.StartedAt).ToList();
        }

        public bool CancelDownload(string downloadId)
        {
            if (!_downloads.TryGetValue(downloadId, out var download))
            {
                return false;
            }

            download.Cts.Cancel();
            return true;
        }

        // Streams the file ourselves (rather than IHttpClient.DownloadFileAsync)
        // so byte progress can be reported live -- the Sites downloads view
        // wants a real percentage/speed, not just a spinner that flips to
        // "done". Keeps DownloadFileAsync's own safeguards: .part file until
        // complete, and reject a text/html response (a landing page that
        // slipped through release resolution) instead of saving it as video.
        private async Task RunDownloadAsync(SiteDownload download, string sourceUrl, CancellationToken token)
        {
            var partPath = download.OutputPath + ".part";

            download.SpeedSampleTime = DateTime.UtcNow;

            try
            {
                _diskProvider.EnsureFolder(Path.GetDirectoryName(download.OutputPath));

                // Best-effort size up front (via HEAD) so the progress bar
                // has a total to fill against while the download runs -- the
                // GetAsync below only returns once the body has fully
                // streamed, so its ContentLength arrives too late to use for
                // live progress. Hosts that don't answer HEAD just leave the
                // bar indeterminate; the byte counter and speed still work.
                try
                {
                    var headResponse = await _httpClient.HeadAsync(new HttpRequest(sourceUrl) { AllowAutoRedirect = true }, token);
                    if (headResponse.Headers.ContentLength is { } length && length > 0)
                    {
                        download.TotalSize = length;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Debug(ex, "HEAD request for size failed for {0}", download.Title);
                }

                await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite))
                await using (var countingStream = new ProgressStream(fileStream, written => UpdateProgress(download, written)))
                {
                    var request = new HttpRequest(sourceUrl)
                    {
                        AllowAutoRedirect = true,
                        ResponseStream = countingStream,
                        RequestTimeout = TimeSpan.FromMinutes(30)
                    };

                    var response = await _httpClient.GetAsync(request, token);

                    if (response.Headers.ContentType != null && response.Headers.ContentType.Contains("text/html"))
                    {
                        throw new HttpException(request, response, "Site responded with html content.");
                    }

                    if (download.TotalSize == 0)
                    {
                        download.TotalSize = response.Headers.ContentLength ?? download.BytesDownloaded;
                    }
                }

                if (File.Exists(download.OutputPath))
                {
                    File.Delete(download.OutputPath);
                }

                File.Move(partPath, download.OutputPath);

                download.BytesDownloaded = download.TotalSize = _diskProvider.GetFileSize(download.OutputPath);
                download.BytesPerSecond = 0;
                download.Status = SiteDownloadStatus.Completed;
                _logger.Info("[{0}] Site download completed -> {1}", download.Title, download.OutputPath);
            }
            catch (OperationCanceledException)
            {
                download.BytesPerSecond = 0;
                download.Status = SiteDownloadStatus.Failed;
                download.Message = "Cancelled.";
            }
            catch (Exception ex)
            {
                download.BytesPerSecond = 0;
                download.Status = SiteDownloadStatus.Failed;
                download.Message = ex.Message;
                _logger.Error(ex, "[{0}] Site download failed", download.Title);
            }
            finally
            {
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }
        }

        // Records bytes written and, roughly once a second, recomputes the
        // running download speed from the delta since the last sample.
        private static void UpdateProgress(SiteDownload download, long written)
        {
            download.BytesDownloaded = written;

            var now = DateTime.UtcNow;
            var elapsed = now - download.SpeedSampleTime;
            if (elapsed.TotalSeconds >= 1)
            {
                download.BytesPerSecond = (long)((written - download.SpeedSampleBytes) / elapsed.TotalSeconds);
                download.SpeedSampleTime = now;
                download.SpeedSampleBytes = written;
            }
        }
    }
}
