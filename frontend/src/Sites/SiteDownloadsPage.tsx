import React, { useCallback } from 'react';
import Icon from 'Components/Icon';
import IconButton from 'Components/Link/IconButton';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import ProgressBar from 'Components/ProgressBar';
import { icons, kinds, sizes } from 'Helpers/Props';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import SiteDownload from './SiteDownload';
import useSiteDownloads, { useCancelSiteDownload } from './useSiteDownloads';
import styles from './SiteDownloadsPage.css';

function statusKind(status: SiteDownload['status']) {
  if (status === 'Completed') {
    return kinds.SUCCESS;
  }

  if (status === 'Failed') {
    return kinds.DANGER;
  }

  return kinds.PRIMARY;
}

function downloadProgress(download: SiteDownload) {
  if (download.totalSize > 0) {
    return Math.min(100, (download.bytesDownloaded / download.totalSize) * 100);
  }

  return download.status === 'Completed' ? 100 : 0;
}

interface DownloadRowProps {
  download: SiteDownload;
  onCancel: (downloadId: string) => void;
}

function DownloadRow({ download, onCancel }: DownloadRowProps) {
  const handleCancel = useCallback(() => {
    onCancel(download.downloadId);
  }, [onCancel, download.downloadId]);

  const speed =
    download.status === 'Downloading' && download.bytesPerSecond > 0
      ? ` · ${formatBytes(download.bytesPerSecond)}/s`
      : '';

  const total =
    download.totalSize > 0 ? ` / ${formatBytes(download.totalSize)}` : '';

  return (
    <div className={styles.row}>
      <div className={styles.rowTop}>
        <span className={styles.title}>{download.title}</span>

        <span className={styles.right}>
          {download.status === 'Downloading' ? (
            <IconButton
              name={icons.REMOVE}
              size={14}
              title={translate('Cancel')}
              onPress={handleCancel}
            />
          ) : (
            <Icon
              name={
                download.status === 'Completed' ? icons.CHECK : icons.WARNING
              }
              size={14}
              kind={statusKind(download.status)}
            />
          )}
        </span>
      </div>

      <ProgressBar
        progress={downloadProgress(download)}
        kind={statusKind(download.status)}
        size={sizes.MEDIUM}
      />

      <div className={styles.rowBottom}>
        <span>
          {formatBytes(download.bytesDownloaded)}
          {total}
          {speed}
        </span>
        <span>{download.message || download.status}</span>
      </div>
    </div>
  );
}

function SiteDownloadsPage() {
  const { data: downloads } = useSiteDownloads();
  const cancelDownload = useCancelSiteDownload();

  const handleCancel = useCallback(
    (downloadId: string) => {
      cancelDownload(downloadId);
    },
    [cancelDownload]
  );

  return (
    <PageContent title={translate('SitesDownloads')}>
      <PageContentBody>
        {downloads.length === 0 ? (
          <div className={styles.empty}>{translate('SitesNoDownloads')}</div>
        ) : null}

        {downloads.map((download) => (
          <DownloadRow
            key={download.downloadId}
            download={download}
            onCancel={handleCancel}
          />
        ))}
      </PageContentBody>
    </PageContent>
  );
}

export default SiteDownloadsPage;
