import { useQueryClient } from '@tanstack/react-query';
import useApiMutation from 'Helpers/Hooks/useApiMutation';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import Series from 'Series/Series';
import SiteShow from './SiteShow';

const DEFAULT_SHOWS: SiteShow[] = [];
const DEFAULT_SERIES: Series[] = [];

// Flat library-series list for the "link to series" picker.
export const useLibrarySeries = (enabled: boolean) => {
  const result = useApiQuery<Series[]>({
    path: '/series',
    queryOptions: {
      enabled,
      staleTime: 5 * 60 * 1000,
    },
  });

  return { ...result, data: result.data ?? DEFAULT_SERIES };
};

// PUT /siteshow/{id}/link -- pin (seriesId 0 clears) the row to a series.
export const useSetSiteShowLink = (showId: number) => {
  const queryClient = useQueryClient();

  return useApiMutation<SiteShow, { seriesId: number; season?: number }>({
    path: `/siteshow/${showId}/link`,
    method: 'PUT',
    mutationOptions: {
      onSuccess: () => {
        queryClient.invalidateQueries({ queryKey: [`/siteshow/${showId}`] });
        queryClient.invalidateQueries({ queryKey: ['/siteshow'] });
        queryClient.invalidateQueries({
          queryKey: [`/siteshow/${showId}/episodes`],
        });
      },
    },
  });
};

// One catalogue show, polled while `enabled` (the detail modal is open).
export const useSiteShow = (showId: number, enabled: boolean) => {
  return useApiQuery<SiteShow>({
    path: `/siteshow/${showId}`,
    queryOptions: {
      enabled: enabled && showId > 0,
      refetchInterval: enabled ? 3000 : false,
    },
  });
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
