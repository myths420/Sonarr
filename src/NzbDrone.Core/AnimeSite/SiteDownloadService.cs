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
using NzbDrone.Core.Indexers.AnimeSite;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.AnimeSite
{
    public interface ISiteDownloadService
    {
        // Resolves releases for the episode and downloads the top pick, or
        // the one matching releaseUrl. Returns null if nothing resolved.
        // When skipIfPresent is set, an episode already in the library is
        // skipped unless the site release parses to a higher quality.
        SiteDownload StartDownload(int showId, int episodeNumber, string releaseUrl = null, bool skipIfPresent = false);

        List<SiteDownload> GetDownloads();

        bool CancelDownload(string downloadId);
    }

    public class SiteDownloadService : ISiteDownloadService
    {
        // One tracker per process, regardless of DI lifetime.
        private static readonly ConcurrentDictionary<string, SiteDownload> _downloads = new();

        // Cap concurrent Sites downloads so a range grab doesn't start 20
        // streams at once. Env override: SITE_MAX_CONCURRENT_DOWNLOADS.
        private static readonly SemaphoreSlim _downloadGate = new(
            int.TryParse(Environment.GetEnvironmentVariable("SITE_MAX_CONCURRENT_DOWNLOADS"), out var m) && m > 0 ? m : 3,
            int.TryParse(Environment.GetEnvironmentVariable("SITE_MAX_CONCURRENT_DOWNLOADS"), out var m2) && m2 > 0 ? m2 : 3);

        private readonly ISiteShowService _siteShowService;
        private readonly ISiteShowRepository _siteShowRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEpisodeService _episodeService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IHttpClient _httpClient;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public SiteDownloadService(ISiteShowService siteShowService,
                                   ISiteShowRepository siteShowRepository,
                                   IRootFolderService rootFolderService,
                                   IManageCommandQueue commandQueueManager,
                                   IEpisodeService episodeService,
                                   IMediaFileService mediaFileService,
                                   IHttpClient httpClient,
                                   IDiskProvider diskProvider,
                                   Logger logger)
        {
            _siteShowService = siteShowService;
            _siteShowRepository = siteShowRepository;
            _rootFolderService = rootFolderService;
            _commandQueueManager = commandQueueManager;
            _episodeService = episodeService;
            _mediaFileService = mediaFileService;
            _httpClient = httpClient;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public SiteDownload StartDownload(int showId, int episodeNumber, string releaseUrl = null, bool skipIfPresent = false)
        {
            var releases = _siteShowService.ResolveEpisodeReleases(showId, episodeNumber);

            // A specific release was asked for -> just that one. Otherwise
            // the whole ordered list, tried top to bottom until one works
            // (direct file hosts first, Dailymotion last).
            var candidates = string.IsNullOrWhiteSpace(releaseUrl)
                ? releases.Where(r => !string.IsNullOrWhiteSpace(r.Url)).ToList()
                : releases.Where(r => string.Equals(r.Url, releaseUrl, StringComparison.OrdinalIgnoreCase)).ToList();

            var release = candidates.FirstOrDefault();
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

            if (skipIfPresent && series != null)
            {
                var haveSeason = SeasonTitleParser.Parse(show.Title).Season;
                if (HaveEqualOrBetterCopy(series, haveSeason, episodeNumber, release))
                {
                    _logger.Debug("Sites: '{0}' S{1:00}E{2:00} already in the library at an equal or better quality -- skipping", show.Title, haveSeason, episodeNumber);
                    return null;
                }
            }

            string outputPath;
            if (series != null && !string.IsNullOrWhiteSpace(series.Path))
            {
                // A "<Show> Season 2" catalogue row is folded into the base
                // series as Season 2 -- name the file for that season so the
                // rescan drops it in the right season folder.
                var season = SeasonTitleParser.Parse(show.Title).Season;
                var fileName = FileNameSafe($"{series.Title} - S{season:00}E{episodeNumber:00} - Episode {episodeNumber}") + ".mp4";
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
            PruneHistory();

            _ = Task.Run(() => RunDownloadAsync(download, candidates, download.Cts.Token));

            return download;
        }

        public List<SiteDownload> GetDownloads()
        {
            return _downloads.Values.OrderByDescending(d => d.StartedAt).ToList();
        }

        // Keep the tracker from growing without bound after a big grab --
        // drop finished entries beyond the most recent 100.
        private static void PruneHistory()
        {
            var finished = _downloads.Values
                .Where(d => d.Status is SiteDownloadStatus.Completed or SiteDownloadStatus.Failed)
                .OrderByDescending(d => d.StartedAt)
                .Skip(100)
                .ToList();

            foreach (var old in finished)
            {
                _downloads.TryRemove(old.DownloadId, out _);
            }
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

        // Tries each candidate release in order until one downloads
        // cleanly. A text/html response (landing page) or any error moves
        // on to the next -- so a dead Mediafire mirror falls through to the
        // Dailymotion stream.
        private async Task RunDownloadAsync(SiteDownload download, List<ResolvedRelease> candidates, CancellationToken token)
        {
            var partPath = download.OutputPath + ".part";

            try
            {
                await _downloadGate.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                download.Status = SiteDownloadStatus.Failed;
                download.Message = "Cancelled.";
                return;
            }

            download.SpeedSampleTime = DateTime.UtcNow;

            try
            {
                _diskProvider.EnsureFolder(Path.GetDirectoryName(download.OutputPath));

                string lastError = null;
                for (var i = 0; i < candidates.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var candidate = candidates[i];
                    download.Title = candidate.Title;

                    try
                    {
                        await DownloadCandidateAsync(download, candidate.Url, partPath, token);

                        download.BytesDownloaded = download.TotalSize = _diskProvider.GetFileSize(download.OutputPath);
                        download.BytesPerSecond = 0;
                        download.Status = SiteDownloadStatus.Completed;
                        _logger.Info("[{0}] Site download completed -> {1}", download.Title, download.OutputPath);

                        if (download.SeriesId.HasValue)
                        {
                            _commandQueueManager.Push(new RescanSeriesCommand(download.SeriesId.Value));
                        }

                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        download.BytesDownloaded = 0;
                        download.TotalSize = 0;
                        var tail = i < candidates.Count - 1 ? " -- trying the next source" : string.Empty;
                        _logger.Warn("[{0}] source {1}/{2} failed: {3}{4}", download.Title, i + 1, candidates.Count, ex.Message, tail);

                        if (File.Exists(partPath))
                        {
                            File.Delete(partPath);
                        }
                    }
                }

                download.BytesPerSecond = 0;
                download.Status = SiteDownloadStatus.Failed;
                download.Message = lastError ?? "No source produced a downloadable file.";
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
                _downloadGate.Release();

                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }
        }

        // Streams one source URL to the .part file and moves it into place.
        // Throws if the response is a web page (landing page) rather than a
        // file, so RunDownloadAsync can move to the next candidate.
        private async Task DownloadCandidateAsync(SiteDownload download, string sourceUrl, string partPath, CancellationToken token)
        {
            download.SpeedSampleTime = DateTime.UtcNow;
            download.TotalSize = 0;

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
                    RequestTimeout = TimeSpan.FromMinutes(60)
                };

                var response = await _httpClient.GetAsync(request, token);

                if (response.Headers.ContentType != null && response.Headers.ContentType.Contains("text/html"))
                {
                    throw new HttpException(request, response, "source returned a web page, not a file");
                }

                if (download.TotalSize == 0)
                {
                    download.TotalSize = response.Headers.ContentLength ?? download.BytesDownloaded;
                }
            }

            if (new FileInfo(partPath).Length == 0)
            {
                throw new InvalidOperationException("source returned an empty file");
            }

            if (File.Exists(download.OutputPath))
            {
                File.Delete(download.OutputPath);
            }

            File.Move(partPath, download.OutputPath);
        }

        // True when the episode already has a file and the site release
        // doesn't parse to a strictly higher quality.
        private bool HaveEqualOrBetterCopy(Series series, int seasonNumber, int episodeNumber, ResolvedRelease release)
        {
            var episode = _episodeService.FindEpisode(series.Id, seasonNumber, episodeNumber);
            if (episode is not { HasFile: true })
            {
                return false;
            }

            var file = episode.EpisodeFileId > 0 ? _mediaFileService.Get(episode.EpisodeFileId) : null;
            if (file?.Quality?.Quality == null)
            {
                // Have something, can't read its quality -- keep it.
                return true;
            }

            var candidate = QualityParser.ParseQuality(release.Title ?? string.Empty);
            if (candidate?.Quality == null || candidate.Quality == Quality.Unknown)
            {
                // Can't tell the site release is any better -- keep existing.
                return true;
            }

            try
            {
                var comparer = new QualityModelComparer(series.QualityProfile.Value);
                return comparer.Compare(candidate, file.Quality) <= 0;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Sites: quality comparison failed for '{0}' -- keeping existing file", series.Title);
                return true;
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
