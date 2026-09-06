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
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Tv;

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
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IHttpClient _httpClient;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public SiteDownloadService(ISiteShowService siteShowService,
                                   ISiteShowRepository siteShowRepository,
                                   IImportListFactory importListFactory,
                                   IManageCommandQueue commandQueueManager,
                                   IHttpClient httpClient,
                                   IDiskProvider diskProvider,
                                   Logger logger)
        {
            _siteShowService = siteShowService;
            _siteShowRepository = siteShowRepository;
            _importListFactory = importListFactory;
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
            var listDefinition = _importListFactory.Get(show.SourceListId);
            var destinationRoot = string.IsNullOrWhiteSpace(listDefinition.RootFolderPath)
                ? Path.GetTempPath()
                : listDefinition.RootFolderPath;

            // Downloading anything from a show pulls it into the Series tab:
            // auto-create the (AniList-backed) series so the file lands in
            // its folder and Sonarr imports/tracks it on the rescan below.
            var series = TryEnsureSeries(showId);

            string outputPath;
            if (series != null && !string.IsNullOrWhiteSpace(series.Path))
            {
                // Drop it straight in the series folder with a name Sonarr's
                // parser can match; the post-download rescan renames it into
                // the season folder per the user's naming config.
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

                if (download.SeriesId.HasValue)
                {
                    // Let Sonarr import the file we just dropped in the
                    // series folder: rename into the season folder, create
                    // the EpisodeFile, mark the episode hasFile.
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
                // No AniList match yet -- fall back to the plain per-show
                // folder; the file still downloads, it just isn't tracked.
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
