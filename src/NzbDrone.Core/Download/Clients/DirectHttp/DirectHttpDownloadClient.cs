using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.DirectHttp
{
    // A "real" download client: unlike SABnzbd/qBittorrent/etc, which hand a
    // link to an external app and poll its status, this one does the actual
    // HTTP resolution + byte transfer itself, in-process. This is a direct
    // port of the working Python pipeline (main.py's landing-page
    // resolution + scraper.py's Download class) into Sonarr's own
    // IDownloadClient interface.
    //
    // SCOPE OF THIS FILE: plain direct-file downloads (the mediafire/
    // mirrored.to/generic-host path). It does NOT yet include the HLS/
    // ffmpeg path or the Dailymotion/Rumble/gdriveplayer embed-resolution
    // logic from hls.py -- that's a separate, larger port and should live
    // in its own resolver class called from RunDownloadAsync below (there's
    // a clearly marked spot for it).
    //
    // KNOWN LIMITATION: progress/queue state lives in an in-memory
    // dictionary (_items). If Sonarr restarts mid-download, that state is
    // lost (the file on disk survives, but Sonarr won't know about it) --
    // none of the built-in clients have this problem because the external
    // app keeps its own state. A production version should persist queue
    // state (e.g. to Sonarr's DB) and requeue on startup.
    public class DirectHttpDownloadClient : DownloadClientBase<DirectHttpDownloadClientSettings>
    {
        private static readonly ConcurrentDictionary<string, DirectDownloadState> _items = new();
        private readonly IHttpClient _httpClient;

        public DirectHttpDownloadClient(IHttpClient httpClient,
                                         IConfigService configService,
                                         IDiskProvider diskProvider,
                                         IRemotePathMappingService remotePathMappingService,
                                         Logger logger,
                                         ILocalizationService localizationService)
            : base(configService, diskProvider, remotePathMappingService, logger, localizationService)
        {
            _httpClient = httpClient;
        }

        public override string Name => "Direct HTTP";

        public override DownloadProtocol Protocol => DownloadProtocol.Unknown;

        public override Task<string> Download(RemoteEpisode remoteEpisode, IIndexer indexer)
        {
            // This is where the custom Indexer (separate piece, not in this
            // file) is expected to have put a resolved-or-one-hop-away URL --
            // see hls.py/main.py's get_server_embeds + _resolve_embed_to_stream
            // for what "resolved" means today in the Python version.
            var sourceUrl = remoteEpisode.Release.DownloadUrl;
            var downloadId = Guid.NewGuid().ToString();
            var title = remoteEpisode.Release.Title;
            var outputPath = BuildOutputPath(title);

            var state = new DirectDownloadState
            {
                DownloadId = downloadId,
                Title = title,
                OutputPath = outputPath,
                Status = DownloadItemStatus.Queued,
                Cts = new CancellationTokenSource(),
            };
            _items[downloadId] = state;

            // Fire-and-forget: GetItems() (polled by Sonarr) reflects
            // progress via the _items dictionary, same shape as scraper.py's
            // ProgressFunction/progress_update_callback pattern.
            _ = Task.Run(() => RunDownloadAsync(state, sourceUrl, state.Cts.Token));

            return Task.FromResult(downloadId);
        }

        private async Task RunDownloadAsync(DirectDownloadState state, string sourceUrl, CancellationToken token)
        {
            state.Status = DownloadItemStatus.Downloading;
            try
            {
                var resolvedUrl = await FinalizeDownloadUrlAsync(sourceUrl, token);

                if (string.IsNullOrEmpty(resolvedUrl))
                {
                    state.Status = DownloadItemStatus.Failed;
                    state.Message = "Could not resolve a real, downloadable file URL (landing page only).";
                    _logger.Warn("[{0}] {1}", state.Title, state.Message);
                    return;
                }

                _diskProvider.EnsureFolder(Path.GetDirectoryName(state.OutputPath));

                // DownloadFileAsync already rejects text/html responses
                // (throws HttpException) -- see HttpClient.DownloadFileAsync
                // in NzbDrone.Common. That's the same safety net as
                // main.py's _looks_like_real_file, already built in here.
                await _httpClient.DownloadFileAsync(resolvedUrl, state.OutputPath, token);

                state.Status = DownloadItemStatus.Completed;
                state.TotalSize = _diskProvider.GetFileSize(state.OutputPath);
                _logger.Info("[{0}] Download completed -> {1}", state.Title, state.OutputPath);
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
        }

        // Port of main.py's _finalize_download_url / _resolve_mirrored_to_link
        // / _resolve_mediafire_link / _looks_like_real_file, minus the final
        // content-type check (DownloadFileAsync already does that).
        private async Task<string> FinalizeDownloadUrlAsync(string url, CancellationToken token)
        {
            if (url.Contains("mediafire.com") && url.Contains("/file/"))
            {
                var resolved = await ResolveMediafireLinkAsync(url, token);
                if (string.IsNullOrEmpty(resolved))
                {
                    return "";
                }

                url = resolved;
            }
            else if (url.Contains("mirrored.to") && url.TrimEnd('/').EndsWith("_links"))
            {
                var resolved = await ResolveMirroredToLinkAsync(url, token);
                if (string.IsNullOrEmpty(resolved))
                {
                    return "";
                }

                url = resolved;
            }

            if (url.Contains("mirrored.to") && url.Contains("dl=0"))
            {
                url = url.Replace("dl=0", "dl=1");
            }

            return url;
        }

        private async Task<string> ResolveMediafireLinkAsync(string mediafireUrl, CancellationToken token)
        {
            try
            {
                var html = await FetchHtmlAsync(mediafireUrl, token);
                var button = html.QuerySelector("a#downloadButton[href], a.input.popsok[href]");
                var href = button?.GetAttribute("href");
                if (!string.IsNullOrEmpty(href) && href.StartsWith("http"))
                {
                    return href;
                }

                _logger.Debug("Mediafire page had no resolvable download button: {0}", mediafireUrl);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to resolve Mediafire link {0}", mediafireUrl);
            }

            return "";
        }

        private async Task<string> ResolveMirroredToLinkAsync(string landingUrl, CancellationToken token)
        {
            try
            {
                var html = await FetchHtmlAsync(landingUrl, token);
                foreach (var a in html.QuerySelectorAll("a[href]"))
                {
                    var href = a.GetAttribute("href");
                    if (string.IsNullOrEmpty(href) || !href.StartsWith("http"))
                    {
                        continue;
                    }

                    if (Regex.IsMatch(href, @"\.(mp4|mkv|avi|m4v)(\?|$)", RegexOptions.IgnoreCase))
                    {
                        return href;
                    }

                    if (href.Contains("mirrored.to") && href.Contains("/files/") && !href.TrimEnd('/').EndsWith("_links"))
                    {
                        return href;
                    }
                }

                _logger.Debug("mirrored.to landing page had no resolvable file link: {0}", landingUrl);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to resolve mirrored.to link {0}", landingUrl);
            }

            return "";
        }

        private async Task<IDocument> FetchHtmlAsync(string url, CancellationToken token)
        {
            var request = new HttpRequest(url) { RequestTimeout = TimeSpan.FromSeconds(15) };
            var response = await _httpClient.GetAsync(request, token);
            var config = AngleSharp.Configuration.Default;
            var context = BrowsingContext.New(config);
            return await context.OpenAsync(req => req.Content(response.Content), token);
        }

        private string BuildOutputPath(string title)
        {
            var safeTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
            var folder = Settings.DestinationDirectory;
            if (!string.IsNullOrWhiteSpace(Settings.Category))
            {
                folder = Path.Combine(folder, Settings.Category);
            }

            // Extension is a best guess until the real resolver (HLS vs
            // direct mp4 vs mkv) runs -- fine for now since the vast
            // majority of sources here are mp4.
            return Path.Combine(folder, safeTitle + ".mp4");
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
                    RemainingSize = state.Status == DownloadItemStatus.Completed ? 0 : state.TotalSize,
                    OutputPath = new OsPath(state.OutputPath),
                    Status = state.Status,
                    Message = state.Message,
                    CanMoveFiles = state.Status == DownloadItemStatus.Completed,
                    CanBeRemoved = true,
                };
            }
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (_items.TryRemove(item.DownloadId, out var state))
            {
                state.Cts.Cancel();
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

        private class DirectDownloadState
        {
            public string DownloadId { get; set; }
            public string Title { get; set; }
            public string OutputPath { get; set; }
            public long TotalSize { get; set; }
            public DownloadItemStatus Status { get; set; }
            public string Message { get; set; }
            public CancellationTokenSource Cts { get; set; }
        }
    }
}
