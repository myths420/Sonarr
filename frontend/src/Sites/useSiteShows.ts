import useApiQuery from 'Helpers/Hooks/useApiQuery';
import SiteShow from './SiteShow';

const DEFAULT_SHOWS: SiteShow[] = [];

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
