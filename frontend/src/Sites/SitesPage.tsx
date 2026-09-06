import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import CommandNames from 'Commands/CommandNames';
import { useCommandExecuting, useExecuteCommand } from 'Commands/useCommands';
import Alert from 'Components/Alert';
import TextInput from 'Components/Form/TextInput';
import Icon from 'Components/Icon';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import { icons, kinds } from 'Helpers/Props';
import { InputChanged } from 'typings/inputs';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import SiteShowCard from './SiteShowCard';
import styles from './SitesPage.css';
import useSites from './useSites';
import useSiteShows from './useSiteShows';

function SitesPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: sites, isFetching: isFetchingSites } = useSites();

  const sourceListId = id ? Number(id) : sites[0]?.id ?? 0;
  const activeSite = sites.find((site) => site.id === sourceListId);

  const [term, setTerm] = useState('');

  // Redirect /sites to the first configured site.
  useEffect(() => {
    if (!id && sites.length > 0) {
      navigate(`/sites/${sites[0].id}`, { replace: true });
    }
  }, [id, sites, navigate]);

  useEffect(() => {
    setTerm('');
  }, [sourceListId]);

  const {
    data: shows,
    isFetching: isFetchingShows,
    isLoading,
    error,
  } = useSiteShows(sourceListId);

  const filteredShows = useMemo(() => {
    const q = term.trim().toLowerCase();
    if (!q) {
      return shows;
    }
    return shows.filter((show) => show.title.toLowerCase().includes(q));
  }, [shows, term]);

  const inLibraryCount = useMemo(
    () => shows.filter((show) => show.seriesId).length,
    [shows]
  );

  const executeCommand = useExecuteCommand();
  const isSyncing = useCommandExecuting(CommandNames.SiteShowSync);

  const handleRefreshPress = useCallback(() => {
    executeCommand({ name: CommandNames.SiteShowSync, sourceListId });
  }, [executeCommand, sourceListId]);

  const handleSearchChange = useCallback(
    ({ value }: InputChanged<string>) => setTerm(value),
    []
  );

  if (isFetchingSites && sites.length === 0) {
    return (
      <PageContent title={translate('Sites')}>
        <PageContentBody>
          <LoadingIndicator />
        </PageContentBody>
      </PageContent>
    );
  }

  if (sites.length === 0) {
    return (
      <PageContent title={translate('Sites')}>
        <PageContentBody>
          <div className={styles.message}>
            {translate('SitesNoneConfigured')}
          </div>
        </PageContentBody>
      </PageContent>
    );
  }

  return (
    <PageContent title={activeSite?.name ?? translate('Sites')}>
      <PageToolbar>
        <PageToolbarSection>
          <PageToolbarButton
            label={translate('Refresh')}
            iconName={icons.REFRESH}
            isSpinning={isSyncing}
            onPress={handleRefreshPress}
          />
        </PageToolbarSection>
      </PageToolbar>

      <PageContentBody>
        {sites.length > 1 ? (
          <div className={styles.siteTabs}>
            {sites.map((site) => (
              <button
                key={site.id}
                type="button"
                className={
                  site.id === sourceListId
                    ? styles.siteTabActive
                    : styles.siteTab
                }
                onClick={() => navigate(`/sites/${site.id}`)}
              >
                {site.name}
              </button>
            ))}
          </div>
        ) : null}

        <div className={styles.searchRow}>
          <div className={styles.searchIcon}>
            <Icon name={icons.SEARCH} size={16} />
          </div>
          <TextInput
            className={styles.searchInput}
            name="siteShowSearch"
            value={term}
            placeholder={translate('SitesSearchPlaceholder')}
            onChange={handleSearchChange}
          />
        </div>

        {isLoading || (isFetchingShows && shows.length === 0) ? (
          <LoadingIndicator />
        ) : null}

        {!isLoading && !!error ? (
          <Alert kind={kinds.DANGER}>{getErrorMessage(error)}</Alert>
        ) : null}

        {!isLoading && !error && shows.length === 0 ? (
          <div className={styles.message}>
            {translate('SitesCatalogueEmpty')}
          </div>
        ) : null}

        {!isLoading && !error && shows.length > 0 ? (
          <>
            <div className={styles.resultCount}>
              {translate('SitesResultCount', { count: filteredShows.length })}
            </div>

            {filteredShows.length > 0 ? (
              <div className={styles.grid}>
                {filteredShows.map((show) => (
                  <SiteShowCard key={show.id} show={show} />
                ))}
              </div>
            ) : (
              <div className={styles.message}>
                {translate('SitesNoMatches', { term })}
              </div>
            )}

            <div className={styles.libraryCount}>
              {translate('SitesLibraryCount', {
                have: inLibraryCount,
                total: shows.length,
              })}
            </div>
          </>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}

export default SitesPage;
