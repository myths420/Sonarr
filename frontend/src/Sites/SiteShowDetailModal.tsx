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
import SiteShow from './SiteShow';
import styles from './SiteShowDetailModal.css';
import useSiteDownloads, { useDownloadEpisodes } from './useSiteDownloads';
import useSiteShowEpisodes, { useEpisodeReleases } from './useSiteShowEpisodes';

interface SiteShowDetailModalProps {
  isOpen: boolean;
  show: SiteShow;
  onModalClose: () => void;
}

interface EpisodeReleasesProps {
  showId: number;
  episodeNumber: number;
  isDownloading: boolean;
  onDownload: (episodeNumber: number, releaseUrl: string) => void;
}

// Expanded under an episode row when "Search" is pressed: the ranked list of
// resolved releases, each with its own Download button.
function EpisodeReleases({
  showId,
  episodeNumber,
  isDownloading,
  onDownload,
}: EpisodeReleasesProps) {
  const { data: releases, isFetching, error } = useEpisodeReleases(
    showId,
    episodeNumber,
    true
  );

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
        <li key={release.url} className={styles.release}>
          <span className={styles.releaseTitle}>{release.title}</span>
          <Button
            kind={kinds.PRIMARY}
            size={sizes.SMALL}
            isDisabled={isDownloading}
            onPress={() => onDownload(episodeNumber, release.url)}
          >
            {translate('Download')}
          </Button>
        </li>
      ))}
    </ul>
  );
}

// Senpwai's "preview" screen: synopsis, episode list, and a download panel
// (episode range -> Download). Per episode there's a Search (list resolved
// releases) and a Download (grab the top pick). The picked release is ranked
// server-side -- English audio/subs, highest quality, most reliable host
// (see ISiteShowService.ResolveEpisodeReleases).
function SiteShowDetailModal({
  isOpen,
  show,
  onModalClose,
}: SiteShowDetailModalProps) {
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
    const startNum = parseInt(start, 10) || 1;
    const endNum = parseInt(end, 10) || Math.max(...availableNumbers, startNum);
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
                  {episodeList.map((episode) => {
                    const download = downloadsByEpisode.get(episode.number);
                    const progress =
                      download && download.totalSize > 0
                        ? Math.min(
                            100,
                            (download.bytesDownloaded / download.totalSize) * 100
                          )
                        : download?.status === 'Completed'
                          ? 100
                          : 0;

                    return (
                      <li key={episode.number} className={styles.episode}>
                        <span className={styles.episodeNumber}>
                          {episode.number}
                        </span>
                        <div className={styles.episodeMain}>
                          <span>{episode.title}</span>

                          <div className={styles.episodeActions}>
                            <Button
                              size={sizes.SMALL}
                              onPress={() =>
                                handleToggleReleases(episode.number)
                              }
                            >
                              {translate('Search')}
                            </Button>
                            <Button
                              kind={kinds.PRIMARY}
                              size={sizes.SMALL}
                              isDisabled={isDownloading}
                              onPress={() => downloadEpisode(episode.number)}
                            >
                              {translate('Download')}
                            </Button>
                          </div>

                          {openReleases === episode.number ? (
                            <EpisodeReleases
                              showId={show.id}
                              episodeNumber={episode.number}
                              isDownloading={isDownloading}
                              onDownload={handleReleaseDownload}
                            />
                          ) : null}

                          {download ? (
                            <div className={styles.episodeProgress}>
                              <ProgressBar
                                progress={progress}
                                kind={
                                  download.status === 'Failed'
                                    ? kinds.DANGER
                                    : download.status === 'Completed'
                                      ? kinds.SUCCESS
                                      : kinds.PRIMARY
                                }
                                size={sizes.SMALL}
                              />
                              <span className={styles.episodeProgressText}>
                                {download.status === 'Downloading'
                                  ? `${formatBytes(download.bytesDownloaded)}${
                                      download.bytesPerSecond > 0
                                        ? ` · ${formatBytes(
                                            download.bytesPerSecond
                                          )}/s`
                                        : ''
                                    }`
                                  : download.message || download.status}
                              </span>
                            </div>
                          ) : null}
                        </div>
                      </li>
                    );
                  })}
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
