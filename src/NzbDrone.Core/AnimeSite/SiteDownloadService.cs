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
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.AnimeSite
{
    public interface ISiteDownloadService
    {
        // Resolves releases for the episode and downloads the top pick, or
        // the one matching releaseUrl. Returns null if nothing resolved.
        SiteDownload StartDownload(int showId, int episodeNumber, string releaseUrl = null);

        List<SiteDownload> GetDownloads();

        bool CancelDownload(string downloadId);
    }

    public class SiteDownloadService : ISiteDownloadService
    {
        // One tracker per process, regardless of DI lifetime.
        private static readonly ConcurrentDictionary<string, SiteDownload> _downloads = new();

        private readonly ISiteShowService _siteShowService;
        private readonly ISiteShowRepository _siteShowRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IHttpClient _httpClient;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public SiteDownloadService(ISiteShowService siteShowService,
                                   ISiteShowRepository siteShowRepository,
                                   IRootFolderService rootFolderService,
                                   IManageCommandQueue commandQueueManager,
                                   IHttpClient httpClient,
                                   IDiskProvider diskProvider,
                                   Logger logger)
        {
            _siteShowService = siteShowService;
            _siteShowRepository = siteShowRepository;
            _rootFolderService = rootFolderService;
            _commandQueueManager = commandQueueManager;
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
            var destinationRoot = _rootFolderService.All().FirstOrDefault()?.Path ?? Path.GetTempPath();

            // Auto-create the series so the file lands in its folder and the
            // post-download rescan imports it.
            var series = TryEnsureSeries(showId);

            string outputPath;
            if (series != null && !string.IsNullOrWhiteSpace(series.Path))
            {
                // Parseable name; the rescan renames it into the season folder.
                var fileName = FileNameSafe($"{series.Title} - S01E{episodeNumber:00} - Episode {episodeNumber}") + ".mp4";
                outputPath = Path.Combine(series.Path, fileName);
            }
            else
            {
                var safeShowTitle = FileNameSafe(show.Title);
                var safeFileName = FileNameSafe($"{show.Title} - Episode {episodeNumber:000}");
                outputPath = Path.Combine(destinationRoot, safeShowTitle, safeFileName + ".mp4");
            }

            var download = new SiteDownload
            {
                DownloadId = Guid.NewGuid().ToString(),
                ShowId = showId,
                EpisodeNumber = episodeNumber,
                Title = release.Title,
                OutputPath = outputPath,
                SeriesId = series?.Id,
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

        // Streamed download with live byte progress. Writes to a .part file
        // until complete and rejects a text/html response (a landing page).
        private async Task RunDownloadAsync(SiteDownload download, string sourceUrl, CancellationToken token)
        {
            var partPath = download.OutputPath + ".part";

            download.SpeedSampleTime = DateTime.UtcNow;

            try
            {
                _diskProvider.EnsureFolder(Path.GetDirectoryName(download.OutputPath));

                // Size up front via HEAD so the progress bar has a total
                // (GetAsync only returns once the body has fully streamed).
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
                        var host = new Uri(sourceUrl).Host.ToLowerInvariant();
                        var isCaptchaHost = host.Contains("vikingfile") || host.Contains("vik1ngfile");

                        throw new HttpException(request, response, isCaptchaHost
                            ? $"{host} serves the file behind a captcha. Open the link in a browser, download the file, and drop it in the series folder; a rescan will import it."
                            : "The download link returned a web page, not a file. The release likely needs a Link Resolution Rule this indexer doesn't have.");
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

                if (download.SeriesId.HasValue)
                {
                    _commandQueueManager.Push(new RescanSeriesCommand(download.SeriesId.Value));
                }
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

        private Series TryEnsureSeries(int showId)
        {
            try
            {
                return _siteShowService.AddAsSeries(showId, null, null, searchForMissingEpisodes: false);
            }
            catch (SiteSeriesAddException ex)
            {
                _logger.Debug("Auto-add to Series tab skipped for site show {0}: {1}", showId, ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Auto-add to Series tab failed for site show {0}", showId);
                return null;
            }
        }

        private static string FileNameSafe(string value)
        {
            return string.Join("_", (value ?? string.Empty).Split(Path.GetInvalidFileNameChars())).Trim();
        }

        // Records bytes written and recomputes the running speed ~1/s.
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
