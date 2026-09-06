import React, { useCallback, useMemo, useState } from 'react';
import Alert from 'Components/Alert';
import TextInput from 'Components/Form/TextInput';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import ProgressBar from 'Components/ProgressBar';
import { kinds, sizes } from 'Helpers/Props';
import { InputChanged } from 'typings/inputs';
import formatBytes from 'Utilities/Number/formatBytes';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import SiteDownload from './SiteDownload';
import SiteShow from './SiteShow';
import SiteShowRelease from './SiteShowRelease';
import useSiteDownloads, { useDownloadEpisodes } from './useSiteDownloads';
import useSiteShowEpisodes, { useEpisodeReleases } from './useSiteShowEpisodes';
import { useSiteShow } from './useSiteShows';
import styles from './SiteShowDetailModal.css';

function progressPercent(download: SiteDownload) {
  if (download.totalSize > 0) {
    return Math.min(100, (download.bytesDownloaded / download.totalSize) * 100);
  }

  return download.status === 'Completed' ? 100 : 0;
}

function progressKind(download: SiteDownload) {
  if (download.status === 'Failed') {
    return kinds.DANGER;
  }

  if (download.status === 'Completed') {
    return kinds.SUCCESS;
  }

  return kinds.PRIMARY;
}

function progressText(download: SiteDownload) {
  if (download.status !== 'Downloading') {
    return download.message || download.status;
  }

  const speed =
    download.bytesPerSecond > 0
      ? ` · ${formatBytes(download.bytesPerSecond)}/s`
      : '';

  return `${formatBytes(download.bytesDownloaded)}${speed}`;
}

interface SiteShowDetailModalProps {
  isOpen: boolean;
  show: SiteShow;
  onModalClose: () => void;
}

interface ReleaseRowProps {
  release: SiteShowRelease;
  episodeNumber: number;
  isDownloading: boolean;
  onDownload: (episodeNumber: number, releaseUrl: string) => void;
}

function ReleaseRow({
  release,
  episodeNumber,
  isDownloading,
  onDownload,
}: ReleaseRowProps) {
  const handleDownload = useCallback(() => {
    onDownload(episodeNumber, release.url);
  }, [onDownload, episodeNumber, release.url]);

  return (
    <li className={styles.release}>
      <span className={styles.releaseTitle}>{release.title}</span>
      <Button
        kind={kinds.PRIMARY}
        size={sizes.SMALL}
        isDisabled={isDownloading}
        onPress={handleDownload}
      >
        {translate('Download')}
      </Button>
    </li>
  );
}

interface EpisodeReleasesProps {
  showId: number;
  episodeNumber: number;
  isDownloading: boolean;
  onDownload: (episodeNumber: number, releaseUrl: string) => void;
}

// The resolved releases for one episode, each with a Download button.
function EpisodeReleases({
  showId,
  episodeNumber,
  isDownloading,
  onDownload,
}: EpisodeReleasesProps) {
  const {
    data: releases,
    isFetching,
    error,
  } = useEpisodeReleases(showId, episodeNumber, true);

  if (isFetching) {
    return (
      <div className={styles.releaseList}>
        <LoadingIndicator size={20} />
      </div>
    );
  }

  if (error) {
    return (
      <div className={styles.releaseList}>
        <Alert kind={kinds.DANGER}>{getErrorMessage(error)}</Alert>
      </div>
    );
  }

  if (releases.length === 0) {
    return (
      <div className={styles.releaseList}>
        <span className={styles.noEpisodes}>
          {translate('SitesNoReleasesFound')}
        </span>
      </div>
    );
  }

  return (
    <ul className={styles.releaseList}>
      {releases.map((release) => (
        <ReleaseRow
          key={release.url}
          release={release}
          episodeNumber={episodeNumber}
          isDownloading={isDownloading}
          onDownload={onDownload}
        />
      ))}
    </ul>
  );
}

interface EpisodeRowProps {
  showId: number;
  episodeNumber: number;
  episodeTitle: string;
  download?: SiteDownload;
  isReleasesOpen: boolean;
  isDownloading: boolean;
  onToggleReleases: (episodeNumber: number) => void;
  onDownload: (episodeNumber: number) => void;
  onReleaseDownload: (episodeNumber: number, releaseUrl: string) => void;
}

function EpisodeRow({
  showId,
  episodeNumber,
  episodeTitle,
  download,
  isReleasesOpen,
  isDownloading,
  onToggleReleases,
  onDownload,
  onReleaseDownload,
}: EpisodeRowProps) {
  const handleToggle = useCallback(() => {
    onToggleReleases(episodeNumber);
  }, [onToggleReleases, episodeNumber]);

  const handleDownload = useCallback(() => {
    onDownload(episodeNumber);
  }, [onDownload, episodeNumber]);

  return (
    <li className={styles.episode}>
      <span className={styles.episodeNumber}>{episodeNumber}</span>
      <div className={styles.episodeMain}>
        <span>{episodeTitle}</span>

        <div className={styles.episodeActions}>
          <Button size={sizes.SMALL} onPress={handleToggle}>
            {translate('Search')}
          </Button>
          <Button
            kind={kinds.PRIMARY}
            size={sizes.SMALL}
            isDisabled={isDownloading}
            onPress={handleDownload}
          >
            {translate('Download')}
          </Button>
        </div>

        {isReleasesOpen ? (
          <EpisodeReleases
            showId={showId}
            episodeNumber={episodeNumber}
            isDownloading={isDownloading}
            onDownload={onReleaseDownload}
          />
        ) : null}

        {download ? (
          <div className={styles.episodeProgress}>
            <ProgressBar
              progress={progressPercent(download)}
              kind={progressKind(download)}
              size={sizes.SMALL}
            />
            <span className={styles.episodeProgressText}>
              {progressText(download)}
            </span>
          </div>
        ) : null}
      </div>
    </li>
  );
}

// Show detail: synopsis, episode list, and per-episode Search / Download
// plus a bulk episode-range download.
function SiteShowDetailModal({
  isOpen,
  show: initialShow,
  onModalClose,
}: SiteShowDetailModalProps) {
  // Refetched while open so seriesId updates after a download auto-adds the series.
  const { data: freshShow } = useSiteShow(initialShow.id, isOpen);
  const show = freshShow ?? initialShow;

  const { title, overview, year, episodes, status, genres, posterUrl, url } =
    show;

  const {
    data: episodeList,
    isFetching,
    error,
  } = useSiteShowEpisodes(show.id, isOpen);

  const { downloadEpisodes, downloadEpisode, isDownloading, downloadError } =
    useDownloadEpisodes(show.id);

  const { data: downloads } = useSiteDownloads();
  const downloadsByEpisode = useMemo(() => {
    const map = new Map<number, (typeof downloads)[number]>();
    downloads
      .filter((d) => d.showId === show.id)
      .forEach((d) => {
        const existing = map.get(d.episodeNumber);

        // Keep the most recent per episode.
        if (!existing || d.startedAt > existing.startedAt) {
          map.set(d.episodeNumber, d);
        }
      });
    return map;
  }, [downloads, show.id]);

  const [start, setStart] = useState('1');
  const [end, setEnd] = useState('');
  const [queued, setQueued] = useState<number[] | null>(null);
  const [openReleases, setOpenReleases] = useState<number | null>(null);

  const availableNumbers = useMemo(
    () => episodeList.map((e) => e.number),
    [episodeList]
  );

  const handleStartChange = useCallback(
    ({ value }: InputChanged<string>) => setStart(value),
    []
  );
  const handleEndChange = useCallback(
    ({ value }: InputChanged<string>) => setEnd(value),
    []
  );

  const handleDownloadPress = useCallback(() => {
    const startNum = Number(start) || 1;
    const endNum = Number(end) || Math.max(...availableNumbers, startNum);
    const range = availableNumbers.filter((n) => n >= startNum && n <= endNum);

    if (range.length > 0) {
      setQueued(range);
      downloadEpisodes(range);
    }
  }, [start, end, availableNumbers, downloadEpisodes]);

  const handleToggleReleases = useCallback((episodeNumber: number) => {
    setOpenReleases((current) =>
      current === episodeNumber ? null : episodeNumber
    );
  }, []);

  const handleReleaseDownload = useCallback(
    (episodeNumber: number, releaseUrl: string) => {
      downloadEpisode(episodeNumber, releaseUrl);
    },
    [downloadEpisode]
  );

  return (
    <Modal isOpen={isOpen} onModalClose={onModalClose}>
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {title}
          {year ? <span className={styles.year}> ({year})</span> : null}
        </ModalHeader>

        <ModalBody>
          <div className={styles.layout}>
            {posterUrl ? (
              <img
                className={styles.poster}
                src={`${window.Sonarr.urlBase}${posterUrl}?apikey=${window.Sonarr.apiKey}`}
                alt=""
              />
            ) : null}

            <div className={styles.info}>
              <div className={styles.labels}>
                {show.seriesId ? (
                  <Label kind={kinds.SUCCESS} size={sizes.LARGE}>
                    {translate('SitesInLibrary')}
                  </Label>
                ) : null}
                {episodes ? (
                  <Label size={sizes.LARGE}>{episodes} eps</Label>
                ) : null}
                {status ? <Label size={sizes.LARGE}>{status}</Label> : null}
                {genres.map((genre) => (
                  <Label key={genre} size={sizes.LARGE}>
                    {genre}
                  </Label>
                ))}
              </div>

              {overview ? (
                <div className={styles.overview}>{overview}</div>
              ) : null}

              {availableNumbers.length > 0 ? (
                <div className={styles.downloadPanel}>
                  <div className={styles.rangeRow}>
                    <span>{translate('Episodes')}</span>
                    <TextInput
                      className={styles.rangeInput}
                      name="startEpisode"
                      type="number"
                      value={start}
                      onChange={handleStartChange}
                    />
                    <span>-</span>
                    <TextInput
                      className={styles.rangeInput}
                      name="endEpisode"
                      type="number"
                      value={end}
                      placeholder={String(Math.max(...availableNumbers))}
                      onChange={handleEndChange}
                    />
                    <Button
                      kind={kinds.PRIMARY}
                      isDisabled={isDownloading}
                      onPress={handleDownloadPress}
                    >
                      {isDownloading
                        ? translate('Downloading')
                        : translate('Download')}
                    </Button>
                  </div>

                  {downloadError ? (
                    <Alert kind={kinds.DANGER}>{downloadError}</Alert>
                  ) : null}

                  {queued && !downloadError ? (
                    <div className={styles.queuedNote}>
                      {translate('SitesQueuedEpisodes', {
                        count: queued.length,
                      })}
                    </div>
                  ) : null}
                </div>
              ) : null}

              <div className={styles.episodesHeader}>
                {translate('Episodes')}
              </div>

              {isFetching ? <LoadingIndicator /> : null}

              {!isFetching && !!error ? (
                <Alert kind={kinds.DANGER}>{getErrorMessage(error)}</Alert>
              ) : null}

              {!isFetching && !error && episodeList.length === 0 ? (
                <div className={styles.noEpisodes}>
                  {translate('SitesNoEpisodesFound')}
                </div>
              ) : null}

              {!isFetching && episodeList.length > 0 ? (
                <ul className={styles.episodeList}>
                  {episodeList.map((episode) => (
                    <EpisodeRow
                      key={episode.number}
                      showId={show.id}
                      episodeNumber={episode.number}
                      episodeTitle={episode.title}
                      download={downloadsByEpisode.get(episode.number)}
                      isReleasesOpen={openReleases === episode.number}
                      isDownloading={isDownloading}
                      onToggleReleases={handleToggleReleases}
                      onDownload={downloadEpisode}
                      onReleaseDownload={handleReleaseDownload}
                    />
                  ))}
                </ul>
              ) : null}
            </div>
          </div>
        </ModalBody>

        <ModalFooter>
          {show.seriesId && show.seriesTitleSlug ? (
            <Link
              className={styles.libraryLink}
              to={`/series/${show.seriesTitleSlug}`}
            >
              {translate('SitesOpenInSonarr')}
            </Link>
          ) : null}
          <Link to={url}>{translate('ViewOnSite')}</Link>
          <Button onPress={onModalClose}>{translate('Close')}</Button>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}

export default SiteShowDetailModal;
