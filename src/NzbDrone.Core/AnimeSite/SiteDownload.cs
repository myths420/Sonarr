using System;
using System.Threading;

namespace NzbDrone.Core.AnimeSite
{
    public enum SiteDownloadStatus
    {
        Downloading,
        Completed,
        Failed
    }

    // In-memory download state for one episode triggered from the Sites
    // catalogue -- deliberately not persisted (same tradeoff
    // DirectHttpDownloadClient makes: lost on restart, but this is a
    // conscious "keep it simple" choice, not an oversight). Not a
    // Sonarr DownloadClientItem/queue entry -- there is no Series/Episode
    // library row for this to attach to.
    public class SiteDownload
    {
        public string DownloadId { get; set; }
        public int ShowId { get; set; }
        public int EpisodeNumber { get; set; }
        public string Title { get; set; }
        public string OutputPath { get; set; }
        public long BytesDownloaded { get; set; }
        public long TotalSize { get; set; }
        public long BytesPerSecond { get; set; }
        public DateTime StartedAt { get; set; }
        public SiteDownloadStatus Status { get; set; }
        public string Message { get; set; }
        public CancellationTokenSource Cts { get; set; }

        // Set when the download's site show was auto-added as a Sonarr
        // series -- on completion the file is dropped in the series folder
        // and a rescan imports/tracks it.
        internal int? SeriesId { get; set; }

        // Sampling state for the BytesPerSecond calc -- updated from the
        // download's progress callback, not meant to be read directly.
        internal DateTime SpeedSampleTime { get; set; }
        internal long SpeedSampleBytes { get; set; }
    }
}
