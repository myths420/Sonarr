import React, { useCallback, useState } from 'react';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import { icons, kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import SiteShow from './SiteShow';
import SiteShowDetailModal from './SiteShowDetailModal';
import styles from './SiteShowCard.css';

interface SiteShowCardProps {
  show: SiteShow;
}

// Catalogue card; opens SiteShowDetailModal on click.
function SiteShowCard({ show }: SiteShowCardProps) {
  const { title, year, episodes, status, genres, posterUrl } = show;
  const [isDetailModalOpen, setIsDetailModalOpen] = useState(false);

  const handlePress = useCallback(() => {
    setIsDetailModalOpen(true);
  }, []);

  const handleModalClose = useCallback(() => {
    setIsDetailModalOpen(false);
  }, []);

  return (
    <div className={styles.card}>
      <button
        type="button"
        className={styles.underlay}
        aria-label={title}
        onClick={handlePress}
      />

      <div className={styles.posterContainer}>
        {posterUrl ? (
          <img
            className={styles.poster}
            src={`${window.Sonarr.urlBase}${posterUrl}?apikey=${window.Sonarr.apiKey}`}
            alt=""
            loading="lazy"
          />
        ) : (
          <div className={styles.posterPlaceholder}>
            <Icon name={icons.SERIES_CONTINUING} size={40} />
          </div>
        )}
      </div>

      <div className={styles.content}>
        <div className={styles.title}>
          {title}
          {year ? <span className={styles.year}> ({year})</span> : null}
        </div>

        <div className={styles.labels}>
          {show.seriesId ? (
            <Label
              kind={
                (show.seriesEpisodeCount ?? 0) > 0 &&
                (show.seriesEpisodeFileCount ?? 0) >= (show.seriesEpisodeCount ?? 0)
                  ? kinds.SUCCESS
                  : kinds.WARNING
              }
              size={sizes.SMALL}
            >
              {(show.seriesEpisodeCount ?? 0) > 0
                ? `${show.seriesEpisodeFileCount ?? 0}/${show.seriesEpisodeCount}`
                : translate('SitesInLibrary')}
            </Label>
          ) : null}

          {!show.seriesId && episodes ? (
            <Label size={sizes.SMALL}>{episodes} eps</Label>
          ) : null}

          {status ? <Label size={sizes.SMALL}>{status}</Label> : null}

          {genres.slice(0, 2).map((genre) => (
            <Label key={genre} size={sizes.SMALL}>
              {genre}
            </Label>
          ))}
        </div>
      </div>

      <SiteShowDetailModal
        isOpen={isDetailModalOpen}
        show={show}
        onModalClose={handleModalClose}
      />
    </div>
  );
}

export default SiteShowCard;
