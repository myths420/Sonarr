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

    // In-memory state for one Sites-catalogue episode download. Not persisted.
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

        // The Sonarr series this download's show was auto-added as, if any.
        internal int? SeriesId { get; set; }

        // Sampling state for the BytesPerSecond calc.
        internal DateTime SpeedSampleTime { get; set; }
        internal long SpeedSampleBytes { get; set; }
    }
}
