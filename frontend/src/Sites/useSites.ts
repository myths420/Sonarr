import { useMemo } from 'react';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import Site from './Site';

const DEFAULT_SITES: Site[] = [];

// All configured import lists, filtered down to AnimeSite instances -- each
// one is a "site" in the Sites catalogue nav. Reuses the existing
// /importlist endpoint rather than adding a parallel one.
const useSites = () => {
  const result = useApiQuery<Site[]>({
    path: '/importlist',
  });

  const sites = useMemo(() => {
    return (result.data ?? DEFAULT_SITES).filter(
      (list) => list.implementation === 'AnimeSiteImportList'
    );
  }, [result.data]);

  return {
    ...result,
    data: sites,
  };
};

export default useSites;
