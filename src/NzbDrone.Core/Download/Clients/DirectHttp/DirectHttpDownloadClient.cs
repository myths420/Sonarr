using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.DirectHttp
{
    // Download client that does the HTTP transfer itself, in-process, for
    // plain direct-file links. Each download goes into its own subfolder of
    // DestinationDirectory so import treats it as a discrete download. Queue
    // state is mirrored to a JSON file under the config folder so a restart
    // mid-import doesn't lose a completed download.
    public class DirectHttpDownloadClient : DownloadClientBase<DirectHttpDownloadClientSettings>
    {
        private static readonly ConcurrentDictionary<string, DirectDownloadState> _items = new();
        private static readonly object _persistLock = new();

        // Caps how many RunDownloadAsync tasks stream at once ("Max
        // Concurrent Downloads"). Static: one process, one pool.
        private static readonly object _gateLock = new();

        private static bool _loaded;
        private static SemaphoreSlim _downloadGate;
        private static int _downloadGateMax;

        private readonly IHttpClient _httpClient;
        private readonly string _statePath;

        public DirectHttpDownloadClient(IHttpClient httpClient,
                                         IConfigService configService,
                                         IDiskProvider diskProvider,
                                         IRemotePathMappingService remotePathMappingService,
                                         IAppFolderInfo appFolderInfo,
                                         Logger logger,
                                         ILocalizationService localizationService)
            : base(configService, diskProvider, remotePathMappingService, logger, localizationService)
        {
            _httpClient = httpClient;
            _statePath = Path.Combine(appFolderInfo.AppDataFolder, "directhttp_downloads.json");

            lock (_persistLock)
            {
                if (!_loaded)
                {
                    LoadState();
                    _loaded = true;
                }
            }
        }

        public override string Name => "Direct HTTP";

        public override DownloadProtocol Protocol => DownloadProtocol.Torrent;

        public override Task<string> Download(RemoteEpisode remoteEpisode, IIndexer indexer)
        {
            var sourceUrl = remoteEpisode.Release.DownloadUrl;
            var downloadId = Guid.NewGuid().ToString();
            var title = remoteEpisode.Release.Title;
            var (downloadFolder, filePath) = BuildOutputPaths(title);

            var state = new DirectDownloadState
            {
                DownloadId = downloadId,
                Title = title,
                DownloadFolder = downloadFolder,
                FilePath = filePath,
                Status = DownloadItemStatus.Queued,
                Cts = new CancellationTokenSource(),
            };
            _items[downloadId] = state;
            PersistState();

            _ = Task.Run(() => RunDownloadAsync(state, sourceUrl, GetDownloadGate(), state.Cts.Token));

            return Task.FromResult(downloadId);
        }

        private SemaphoreSlim GetDownloadGate()
        {
            var max = Settings.MaxConcurrentDownloads > 0 ? Settings.MaxConcurrentDownloads : 3;
            lock (_gateLock)
            {
                if (_downloadGate == null || _downloadGateMax != max)
                {
                    _downloadGate = new SemaphoreSlim(max, max);
                    _downloadGateMax = max;
                }

                return _downloadGate;
            }
        }

        private async Task RunDownloadAsync(DirectDownloadState state, string sourceUrl, SemaphoreSlim gate, CancellationToken token)
        {
            try
            {
                await gate.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                state.Status = DownloadItemStatus.Failed;
                state.Message = "Cancelled.";
                PersistState();
                return;
            }

            state.Status = DownloadItemStatus.Downloading;
            PersistState();

            try
            {
                if (string.IsNullOrEmpty(sourceUrl))
                {
                    Fail(state, "No download URL was provided.");
                    return;
                }

                _diskProvider.EnsureFolder(state.DownloadFolder);

                // HEAD first so the queue has a total for progress.
                try
                {
                    var head = await _httpClient.HeadAsync(new HttpRequest(sourceUrl) { AllowAutoRedirect = true }, token);
                    if (head.Headers.ContentLength is { } length && length > 0)
                    {
                        state.TotalSize = length;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Debug(ex, "HEAD request for size failed for {0}", state.Title);
                }

                var partPath = state.FilePath + ".part";

                await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite))
                await using (var progressStream = new ProgressStream(fileStream, written => state.BytesDownloaded = written))
                {
                    var request = new HttpRequest(sourceUrl)
                    {
                        AllowAutoRedirect = true,
                        ResponseStream = progressStream,
                        RequestTimeout = TimeSpan.FromMinutes(30)
                    };

                    var response = await _httpClient.GetAsync(request, token);

                    // A text/html body is a landing page, not the video.
                    if (response.Headers.ContentType != null && response.Headers.ContentType.Contains("text/html"))
                    {
                        throw new HttpException(request, response, "Site responded with html content.");
                    }
                }

                if (_diskProvider.FileExists(state.FilePath))
                {
                    _diskProvider.DeleteFile(state.FilePath);
                }

                _diskProvider.MoveFile(partPath, state.FilePath);

                state.Status = DownloadItemStatus.Completed;
                state.BytesDownloaded = state.TotalSize = _diskProvider.GetFileSize(state.FilePath);
                _logger.Info("[{0}] Download completed -> {1}", state.Title, state.FilePath);
            }
            catch (OperationCanceledException)
            {
                state.Status = DownloadItemStatus.Failed;
                state.Message = "Cancelled.";
            }
            catch (Exception ex)
            {
                state.Status = DownloadItemStatus.Failed;
                state.Message = ex.Message;
                _logger.Error(ex, "[{0}] Download failed", state.Title);
            }
            finally
            {
                gate.Release();

                var partPath = state.FilePath + ".part";
                if (_diskProvider.FileExists(partPath))
                {
                    _diskProvider.DeleteFile(partPath);
                }

                PersistState();
            }
        }

        private void Fail(DirectDownloadState state, string message)
        {
            state.Status = DownloadItemStatus.Failed;
            state.Message = message;
            _logger.Warn("[{0}] {1}", state.Title, message);
        }

        private (string DownloadFolder, string FilePath) BuildOutputPaths(string title)
        {
            var safeTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
            var root = Settings.DestinationDirectory;
            if (!string.IsNullOrWhiteSpace(Settings.Category))
            {
                root = Path.Combine(root, Settings.Category);
            }

            var downloadFolder = Path.Combine(root, safeTitle);

            return (downloadFolder, Path.Combine(downloadFolder, safeTitle + ".mp4"));
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            foreach (var state in _items.Values)
            {
                yield return new DownloadClientItem
                {
                    DownloadId = state.DownloadId,
                    Title = state.Title,
                    TotalSize = state.TotalSize,
                    RemainingSize = state.Status == DownloadItemStatus.Completed
                        ? 0
                        : Math.Max(0, state.TotalSize - state.BytesDownloaded),
                    OutputPath = new OsPath(state.DownloadFolder),
                    Status = state.Status,
                    Message = state.Message,
                    CanMoveFiles = state.Status == DownloadItemStatus.Completed,
                    CanBeRemoved = true,
                    DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                };
            }
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (_items.TryRemove(item.DownloadId, out var state))
            {
                state.Cts?.Cancel();
                PersistState();
            }

            if (deleteData)
            {
                DeleteItemData(item);
            }
        }

        public override DownloadClientInfo GetStatus()
        {
            return new DownloadClientInfo
            {
                IsLocalhost = true,
                OutputRootFolders = new List<OsPath> { new(Settings.DestinationDirectory) },
            };
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            var folderFailure = TestFolder(Settings.DestinationDirectory, nameof(Settings.DestinationDirectory));
            if (folderFailure != null)
            {
                failures.Add(folderFailure);
            }
        }

        private void PersistState()
        {
            lock (_persistLock)
            {
                try
                {
                    var snapshot = _items.Values.Select(PersistedDownload.From).ToList();
                    _diskProvider.WriteAllText(_statePath, JsonSerializer.Serialize(snapshot));
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to persist DirectHttp download queue");
                }
            }
        }

        private void LoadState()
        {
            try
            {
                if (!_diskProvider.FileExists(_statePath))
                {
                    return;
                }

                var snapshot = JsonSerializer.Deserialize<List<PersistedDownload>>(_diskProvider.ReadAllText(_statePath))
                               ?? new List<PersistedDownload>();

                _logger.Debug("DirectHttp: reloading {0} download(s) from persisted state", snapshot.Count);

                foreach (var item in snapshot)
                {
                    var status = item.Status;

                    // On reload, an unfinished download becomes Completed if
                    // its file is fully on disk, else Failed.
                    if (status is DownloadItemStatus.Downloading or DownloadItemStatus.Queued)
                    {
                        status = _diskProvider.FileExists(item.FilePath) && new FileInfo(item.FilePath).Length > 0
                            ? DownloadItemStatus.Completed
                            : DownloadItemStatus.Failed;
                    }

                    _items[item.DownloadId] = new DirectDownloadState
                    {
                        DownloadId = item.DownloadId,
                        Title = item.Title,
                        DownloadFolder = item.DownloadFolder,
                        FilePath = item.FilePath,
                        TotalSize = item.TotalSize,
                        Status = status,
                        Message = status == DownloadItemStatus.Failed && item.Status != DownloadItemStatus.Failed
                            ? "Interrupted by a restart."
                            : item.Message,
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to load persisted DirectHttp download queue");
            }
        }

        private class DirectDownloadState
        {
            public string DownloadId { get; set; }
            public string Title { get; set; }
            public string DownloadFolder { get; set; }
            public string FilePath { get; set; }
            public long BytesDownloaded { get; set; }
            public long TotalSize { get; set; }
            public DownloadItemStatus Status { get; set; }
            public string Message { get; set; }
            public CancellationTokenSource Cts { get; set; }
        }

        private class PersistedDownload
        {
            public string DownloadId { get; set; }
            public string Title { get; set; }
            public string DownloadFolder { get; set; }
            public string FilePath { get; set; }
            public long TotalSize { get; set; }
            public DownloadItemStatus Status { get; set; }
            public string Message { get; set; }

            public static PersistedDownload From(DirectDownloadState state)
            {
                return new PersistedDownload
                {
                    DownloadId = state.DownloadId,
                    Title = state.Title,
                    DownloadFolder = state.DownloadFolder,
                    FilePath = state.FilePath,
                    TotalSize = state.TotalSize,
                    Status = state.Status,
                    Message = state.Message,
                };
            }
        }
    }
}
