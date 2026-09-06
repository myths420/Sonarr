import useApiQuery from 'Helpers/Hooks/useApiQuery';
import SiteShow from './SiteShow';

const DEFAULT_SHOWS: SiteShow[] = [];

// One catalogue show, kept fresh while its detail modal is open -- picks
// up seriesId/seriesTitleSlug within a few seconds of a download starting
// (which auto-creates the Sonarr series).
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
