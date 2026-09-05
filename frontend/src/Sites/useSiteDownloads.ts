import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useState } from 'react';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import fetchJson from 'Utilities/Fetch/fetchJson';
import getQueryPath from 'Utilities/Fetch/getQueryPath';
import SiteDownload from './SiteDownload';

const DEFAULT_DOWNLOADS: SiteDownload[] = [];

export const useSiteDownloads = () => {
  const result = useApiQuery<SiteDownload[]>({
    path: '/sitedownload',
    queryOptions: {
      refetchInterval: 2000,
    },
  });

  return {
    ...result,
    data: result.data ?? DEFAULT_DOWNLOADS,
  };
};

// The download endpoint is per (show, episode), so the path can't be fixed
// on a hook -- call fetchJson directly with the path built at click time.
export const useDownloadEpisodes = (showId: number) => {
  const queryClient = useQueryClient();
  const [isDownloading, setIsDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const startOne = useCallback(
    async (number: number, releaseUrl?: string) => {
      const query = releaseUrl
        ? `?releaseUrl=${encodeURIComponent(releaseUrl)}`
        : '';

      await fetchJson({
        path: getQueryPath(
          `/siteshow/${showId}/episodes/${number}/download${query}`
        ),
        method: 'POST',
        headers: {
          'X-Api-Key': window.Sonarr.apiKey,
          'X-Sonarr-Client': 'Sonarr',
        },
      });
    },
    [showId]
  );

  const downloadEpisodes = useCallback(
    async (episodeNumbers: number[]) => {
      setIsDownloading(true);
      setError(null);

      try {
        for (const number of episodeNumbers) {
          await startOne(number);
        }

        queryClient.invalidateQueries({ queryKey: ['/sitedownload'] });
      } catch (e) {
        setError(
          e instanceof Error ? e.message : 'Failed to start one or more downloads'
        );
      } finally {
        setIsDownloading(false);
      }
    },
    [startOne, queryClient]
  );

  // Single episode, optionally a specific release chosen from Search results.
  const downloadEpisode = useCallback(
    async (number: number, releaseUrl?: string) => {
      setIsDownloading(true);
      setError(null);

      try {
        await startOne(number, releaseUrl);
        queryClient.invalidateQueries({ queryKey: ['/sitedownload'] });
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Failed to start download');
      } finally {
        setIsDownloading(false);
      }
    },
    [startOne, queryClient]
  );

  return {
    downloadEpisodes,
    downloadEpisode,
    isDownloading,
    downloadError: error,
  };
};

export const useCancelSiteDownload = () => {
  const queryClient = useQueryClient();

  return useCallback(
    async (downloadId: string) => {
      await fetchJson({
        path: getQueryPath(`/sitedownload/${downloadId}`),
        method: 'DELETE',
        headers: {
          'X-Api-Key': window.Sonarr.apiKey,
          'X-Sonarr-Client': 'Sonarr',
        },
      });

      queryClient.invalidateQueries({ queryKey: ['/sitedownload'] });
    },
    [queryClient]
  );
};

export default useSiteDownloads;
