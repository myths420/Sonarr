import React, { useCallback } from 'react';
import Icon from 'Components/Icon';
import IconButton from 'Components/Link/IconButton';
import ProgressBar from 'Components/ProgressBar';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import { icons, kinds, sizes } from 'Helpers/Props';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import SiteDownload from './SiteDownload';
import styles from './SiteDownloadsPage.css';
import useSiteDownloads, { useCancelSiteDownload } from './useSiteDownloads';

function statusKind(status: SiteDownload['status']) {
  if (status === 'Completed') {
    return kinds.SUCCESS;
  }

  if (status === 'Failed') {
    return kinds.DANGER;
  }

  return kinds.PRIMARY;
}

function SiteDownloadsPage() {
  const { data: downloads, isLoading } = useSiteDownloads();
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
        {isLoading ? null : null}

        {downloads.length === 0 ? (
          <div className={styles.empty}>{translate('SitesNoDownloads')}</div>
        ) : null}

        {downloads.map((download) => {
          const progress =
            download.totalSize > 0
              ? Math.min(
                  100,
                  (download.bytesDownloaded / download.totalSize) * 100
                )
              : download.status === 'Completed'
                ? 100
                : 0;

          return (
            <div key={download.downloadId} className={styles.row}>
              <div className={styles.rowTop}>
                <span className={styles.title}>{download.title}</span>

                <span className={styles.right}>
                  {download.status === 'Downloading' ? (
                    <IconButton
                      name={icons.REMOVE}
                      size={14}
                      title={translate('Cancel')}
                      onPress={() => handleCancel(download.downloadId)}
                    />
                  ) : (
                    <Icon
                      name={
                        download.status === 'Completed'
                          ? icons.CHECK
                          : icons.WARNING
                      }
                      size={14}
                      kind={statusKind(download.status)}
                    />
                  )}
                </span>
              </div>

              <ProgressBar
                progress={progress}
                kind={statusKind(download.status)}
                size={sizes.MEDIUM}
              />

              <div className={styles.rowBottom}>
                <span>
                  {formatBytes(download.bytesDownloaded)}
                  {download.totalSize > 0
                    ? ` / ${formatBytes(download.totalSize)}`
                    : ''}
                  {download.status === 'Downloading' &&
                  download.bytesPerSecond > 0
                    ? ` · ${formatBytes(download.bytesPerSecond)}/s`
                    : ''}
                </span>
                <span>{download.message || download.status}</span>
              </div>
            </div>
          );
        })}
      </PageContentBody>
    </PageContent>
  );
}

export default SiteDownloadsPage;
