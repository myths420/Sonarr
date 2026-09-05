import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useState } from 'react';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import fetchJson from 'Utilities/Fetch/fetchJson';
import getQueryPath from 'Utilities/Fetch/getQueryPath';
import SiteShow from './SiteShow';

const DEFAULT_SHOWS: SiteShow[] = [];

// Creates a real (AniList-backed) Sonarr series for a catalogue show. On
// success the returned SiteShow carries seriesId/seriesTitleSlug.
export const useAddSiteShowAsSeries = () => {
  const queryClient = useQueryClient();
  const [isAdding, setIsAdding] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const addAsSeries = useCallback(
    async (showId: number): Promise<SiteShow | null> => {
      setIsAdding(true);
      setError(null);

      try {
        const result = (await fetchJson({
          path: getQueryPath(`/siteshow/${showId}/add`),
          method: 'POST',
          headers: {
            'X-Api-Key': window.Sonarr.apiKey,
            'X-Sonarr-Client': 'Sonarr',
          },
          body: {},
        })) as SiteShow;

        queryClient.invalidateQueries({ queryKey: ['/siteshow'] });
        queryClient.invalidateQueries({ queryKey: ['/series'] });

        return result;
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Failed to add series');
        return null;
      } finally {
        setIsAdding(false);
      }
    },
    [queryClient]
  );

  return { addAsSeries, isAdding, addError: error };
};

const useSiteShows = (sourceListId: number) => {
  const result = useApiQuery<SiteShow[]>({
    path: '/siteshow',
    queryParams: { sourceListId },
    queryOptions: {
      enabled: sourceListId > 0,
    },
  });

  return {
    ...result,
    data: result.data ?? DEFAULT_SHOWS,
  };
};

export default useSiteShows;
