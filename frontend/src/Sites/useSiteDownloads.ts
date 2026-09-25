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

// Per (show, episode); the path is built at call time.
export const useDownloadEpisodes = (showId: number) => {
  const queryClient = useQueryClient();
  const [isDownloading, setIsDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const startOne = useCallback(
    async (number: number, releaseUrl?: string, skipIfPresent = false) => {
      const params = new URLSearchParams();
      if (releaseUrl) {
        params.set('releaseUrl', releaseUrl);
      }

      if (skipIfPresent) {
        params.set('skipIfPresent', 'true');
      }

      const query = params.toString() ? `?${params.toString()}` : '';

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

  // A bulk range shouldn't re-fetch episodes you already have, and one
  // episode failing (no release resolved, a transient error, ...)
  // shouldn't stop every episode after it from being attempted.
  const downloadEpisodes = useCallback(
    async (episodeNumbers: number[]) => {
      setIsDownloading(true);
      setError(null);

      const failures: number[] = [];

      for (const number of episodeNumbers) {
        try {
          await startOne(number, undefined, true);
        } catch {
          failures.push(number);
        }
      }

      queryClient.invalidateQueries({ queryKey: ['/sitedownload'] });
      setIsDownloading(false);

      if (failures.length > 0) {
        setError(
          `Couldn't resolve a release for episode${failures.length > 1 ? 's' : ''} ${failures.join(', ')} -- the rest were queued.`
        );
      }
    },
    [startOne, queryClient]
  );

  // Single episode, optionally a specific release chosen from Search results.
  // Always forces the download (skipIfPresent=false) -- this is the
  // explicit Download/Redownload button on one row.
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
