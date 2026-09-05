export interface SiteDownload {
  downloadId: string;
  showId: number;
  episodeNumber: number;
  title: string;
  outputPath: string;
  bytesDownloaded: number;
  totalSize: number;
  bytesPerSecond: number;
  startedAt: string;
  status: 'Downloading' | 'Completed' | 'Failed';
  message?: string;
}

export default SiteDownload;
