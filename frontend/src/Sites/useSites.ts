import { useMemo } from 'react';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import Site from './Site';

const DEFAULT_SITES: Site[] = [];

// A "site" is exactly one AnimeSite indexer -- add one under
// Settings > Indexers and it shows up here; delete it and it's gone.
// Nothing is preconfigured.
const useSites = () => {
  const result = useApiQuery<Site[]>({
    path: '/indexer',
  });

  const sites = useMemo(() => {
    return (result.data ?? DEFAULT_SITES).filter(
      (indexer) => indexer.implementation === 'AnimeSiteIndexer'
    );
  }, [result.data]);

  return {
    ...result,
    data: sites,
  };
};

export default useSites;
