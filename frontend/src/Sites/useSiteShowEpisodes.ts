import useApiQuery from 'Helpers/Hooks/useApiQuery';
import SiteShowEpisode from './SiteShowEpisode';
import SiteShowRelease from './SiteShowRelease';

const DEFAULT_EPISODES: SiteShowEpisode[] = [];
const DEFAULT_RELEASES: SiteShowRelease[] = [];

const useSiteShowEpisodes = (showId: number, enabled: boolean) => {
  const result = useApiQuery<SiteShowEpisode[]>({
    path: `/siteshow/${showId}/episodes`,
    queryOptions: {
      enabled: enabled && showId > 0,
      staleTime: 60 * 1000,
    },
  });

  return {
    ...result,
    data: result.data ?? DEFAULT_EPISODES,
  };
};

// Resolved releases for one episode, ranked; the first is the default pick.
export const useEpisodeReleases = (
  showId: number,
  episodeNumber: number,
  enabled: boolean
) => {
  const result = useApiQuery<SiteShowRelease[]>({
    path: `/siteshow/${showId}/episodes/${episodeNumber}/releases`,
    queryOptions: {
      enabled: enabled && showId > 0 && episodeNumber > 0,
      staleTime: 60 * 1000,
    },
  });

  return {
    ...result,
    data: result.data ?? DEFAULT_RELEASES,
  };
};

export default useSiteShowEpisodes;
