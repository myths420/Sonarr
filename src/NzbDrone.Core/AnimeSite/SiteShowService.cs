using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.AnimeSite;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.AnimeSite;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource.AniList;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.AnimeSite
{
    public interface ISiteShowService
    {
        List<SiteShow> GetForSourceList(int sourceListId);
        SiteShow Get(int id);

        // Browses the site and upserts every show it finds (title/url only
        // -- fast, one sync). Metadata (poster/overview/genres) is filled in
        // separately by BackfillMetadata so a single sync can't get stuck
        // making hundreds of AniList calls inline.
        int SyncCatalogue(int sourceListId);

        // Looks up metadata for up to `limit` poster-less shows on this
        // list. `force` (manual Refresh) retries every one regardless of
        // when it was last attempted; otherwise a failed lookup backs off a
        // few days so a scheduled run doesn't hammer a dead metadata source.
        int BackfillMetadata(int sourceListId, int limit, bool force = false);

        // Fetched live (not cached) for the show detail view -- episode
        // lists change as a show airs, and this is a single page fetch, not
        // a whole-catalogue walk.
        List<AnimeSiteEpisodeEntry> GetEpisodes(int showId);

        // Resolves real, directly-fetchable download link(s) for one
        // episode. Borrows link-resolution settings (DirectDownloadHosts/
        // LinkResolutionRules/ScrapingScript's getReleases()) from whichever
        // AnimeSiteIndexer instance shares this show's site BaseUrl -- the
        // Sites catalogue (an Import List) has no Series/Episode to search
        // for, so it can never go through that indexer's own search path,
        // but there is no reason to duplicate its settings just for this.
        // Returns an empty list (not an exception) if no matching indexer is
        // configured, or the requested episode number isn't found.
        List<ResolvedRelease> ResolveEpisodeReleases(int showId, int episodeNumber);

        // Creates a real Sonarr Series for this catalogue show so it appears
        // in the Series tab and gets Sonarr's monitoring / daily new-episode
        // handling. Backed by AniList (not TheTVDB) via a synthetic id -- see
        // AniListSeriesIds. Throws SiteSeriesAddException if the show has no
        // AniList match (nothing to build episodes/air-dates from yet).
        Series AddAsSeries(int showId, string rootFolderPath, int? qualityProfileId, bool searchForMissingEpisodes);
    }

    public class SiteSeriesAddException : Exception
    {
        public SiteSeriesAddException(string message)
            : base(message)
        {
        }
    }

    public class SiteShowService : ISiteShowService, IExecute<SiteShowSyncCommand>, IHandleAsync<ProviderDeletedEvent<IImportList>>
    {
        private const int DefaultBackfillLimit = 25;

        private readonly ISiteShowRepository _repository;
        private readonly IImportListFactory _importListFactory;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IAnimeSiteCatalogBrowser _catalogBrowser;
        private readonly IAnimeSiteReleaseResolver _releaseResolver;
        private readonly IShowMetadataProvider _metadataProvider;
        private readonly ISiteScrapeMetadataProvider _scrapeMetadataProvider;
        private readonly ISiteShowPosterService _posterService;
        private readonly IAddSeriesService _addSeriesService;
        private readonly ISeriesService _seriesService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public SiteShowService(ISiteShowRepository repository,
                               IImportListFactory importListFactory,
                               IIndexerFactory indexerFactory,
                               IAnimeSiteCatalogBrowser catalogBrowser,
                               IAnimeSiteReleaseResolver releaseResolver,
                               IShowMetadataProvider metadataProvider,
                               ISiteScrapeMetadataProvider scrapeMetadataProvider,
                               ISiteShowPosterService posterService,
                               IAddSeriesService addSeriesService,
                               ISeriesService seriesService,
                               IRootFolderService rootFolderService,
                               IQualityProfileService qualityProfileService,
                               IHttpClient httpClient,
                               Logger logger)
        {
            _repository = repository;
            _importListFactory = importListFactory;
            _indexerFactory = indexerFactory;
            _catalogBrowser = catalogBrowser;
            _releaseResolver = releaseResolver;
            _metadataProvider = metadataProvider;
            _scrapeMetadataProvider = scrapeMetadataProvider;
            _posterService = posterService;
            _addSeriesService = addSeriesService;
            _seriesService = seriesService;
            _rootFolderService = rootFolderService;
            _qualityProfileService = qualityProfileService;
            _httpClient = httpClient;
            _logger = logger;
        }

        public List<SiteShow> GetForSourceList(int sourceListId)
        {
            return _repository.FindBySourceList(sourceListId);
        }

        public SiteShow Get(int id)
        {
            return _repository.Get(id);
        }

        public int SyncCatalogue(int sourceListId)
        {
            var definition = _importListFactory.Get(sourceListId);
            var settings = (AnimeSiteImportListSettings)definition.Settings;

            var entries = _catalogBrowser.Browse(settings, _logger);
            var existing = _repository.FindBySourceList(sourceListId).ToDictionary(s => s.Slug, StringComparer.OrdinalIgnoreCase);

            var toAdd = new List<SiteShow>();
            var toUpdate = new List<SiteShow>();

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Url))
                {
                    continue;
                }

                var slug = SlugFromUrl(entry.Url);
                if (existing.TryGetValue(slug, out var show))
                {
                    show.Title = entry.Title;
                    show.Url = entry.Url;
                    toUpdate.Add(show);
                    continue;
                }

                toAdd.Add(new SiteShow
                {
                    SourceListId = sourceListId,
                    Slug = slug,
                    Title = entry.Title,
                    Url = entry.Url,
                    LastSyncTime = DateTime.UtcNow
                });
            }

            _repository.InsertMany(toAdd);
            _repository.UpdateMany(toUpdate);

            _logger.Info("AnimeSite catalogue sync for list {0}: {1} added, {2} updated", sourceListId, toAdd.Count, toUpdate.Count);

            return toAdd.Count + toUpdate.Count;
        }

        public int BackfillMetadata(int sourceListId, int limit, bool force = false)
        {
            var take = limit > 0 ? limit : DefaultBackfillLimit;
            var retryBefore = DateTime.UtcNow.AddDays(-3);

            // A show still needs metadata if it has no poster. On a scheduled
            // run, back a failed lookup off for a few days rather than
            // retrying it every time; a manual Refresh retries them all.
            var pending = _repository.FindBySourceList(sourceListId)
                .Where(s => string.IsNullOrEmpty(s.PosterUrl) &&
                            (force || s.LastSyncTime == default || s.LastSyncTime < retryBefore))
                .Take(take)
                .ToList();

            var updated = new List<SiteShow>();
            var aniListHits = 0;
            var scrapeHits = 0;

            foreach (var show in pending)
            {
                show.LastSyncTime = DateTime.UtcNow;

                var metadata = _metadataProvider.Lookup(show.Title);
                if (metadata != null)
                {
                    aniListHits++;
                }
                else
                {
                    metadata = _scrapeMetadataProvider.ScrapeFromPage(show.Url);
                    if (metadata != null)
                    {
                        scrapeHits++;
                    }
                }

                if (metadata != null)
                {
                    // Merge non-empty fields only, so a later run from a
                    // better source can fill gaps without wiping what's there.
                    if (metadata.AniListId > 0)
                    {
                        show.AniListId = metadata.AniListId;
                    }

                    if (!string.IsNullOrWhiteSpace(metadata.PosterUrl))
                    {
                        show.PosterUrl = metadata.PosterUrl;
                    }

                    if (!string.IsNullOrWhiteSpace(metadata.Overview))
                    {
                        show.Overview = metadata.Overview;
                    }

                    if (metadata.Year > 0)
                    {
                        show.Year = metadata.Year;
                    }

                    if (metadata.Episodes > 0)
                    {
                        show.Episodes = metadata.Episodes;
                    }

                    if (!string.IsNullOrWhiteSpace(metadata.Status))
                    {
                        show.Status = metadata.Status;
                    }

                    if (metadata.Genres is { Count: > 0 })
                    {
                        show.Genres = string.Join(",", metadata.Genres);
                    }

                    _posterService.PreCache(show);
                }

                updated.Add(show);
            }

            _repository.UpdateMany(updated);

            _logger.Info("AnimeSite metadata backfill for list {0}: {1} processed ({2} via AniList, {3} via site scrape)", sourceListId, updated.Count, aniListHits, scrapeHits);

            return updated.Count;
        }

        public List<AnimeSiteEpisodeEntry> GetEpisodes(int showId)
        {
            var show = _repository.Get(showId);
            var definition = _importListFactory.Get(show.SourceListId);
            var settings = (AnimeSiteImportListSettings)definition.Settings;

            return _catalogBrowser.BrowseEpisodes(settings, show.Url, _logger);
        }

        public List<ResolvedRelease> ResolveEpisodeReleases(int showId, int episodeNumber)
        {
            var show = _repository.Get(showId);
            var listDefinition = _importListFactory.Get(show.SourceListId);
            var listSettings = (AnimeSiteImportListSettings)listDefinition.Settings;

            var indexerSettings = FindMatchingIndexerSettings(listSettings.BaseUrl);
            if (indexerSettings == null)
            {
                _logger.Warn("No AnimeSite indexer configured for {0} -- add one with the same Website URL to enable downloads from the Sites catalogue.", listSettings.BaseUrl);
                return new List<ResolvedRelease>();
            }

            var episodes = _catalogBrowser.BrowseEpisodes(listSettings, show.Url, _logger);
            var episode = episodes.FirstOrDefault(e => e.Number == episodeNumber);
            if (episode == null)
            {
                _logger.Debug("Episode {0} not found for {1}", episodeNumber, show.Title);
                return new List<ResolvedRelease>();
            }

            string episodeHtml;
            try
            {
                episodeHtml = _httpClient.Get(new HttpRequest(episode.Url)).Content;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to fetch episode page {0}", episode.Url);
                return new List<ResolvedRelease>();
            }

            var options = AnimeSiteReleaseOptions.FromSettings(indexerSettings);
            return _releaseResolver.GetReleases(options, episodeHtml, episode.Url, show.Title, episodeNumber, _logger);
        }

        public Series AddAsSeries(int showId, string rootFolderPath, int? qualityProfileId, bool searchForMissingEpisodes)
        {
            var show = _repository.Get(showId);
            if (show == null)
            {
                throw new SiteSeriesAddException("Site show not found.");
            }

            var aniListId = show.AniListId;
            if (aniListId <= 0)
            {
                // No id stored yet -- try a live lookup so the user doesn't
                // have to wait for the next metadata backfill.
                aniListId = _metadataProvider.Lookup(show.Title)?.AniListId ?? 0;
            }

            if (aniListId <= 0)
            {
                throw new SiteSeriesAddException(
                    $"'{show.Title}' has no AniList match yet, so a tracked series (with episode air dates) can't be created. Try again after the next metadata refresh.");
            }

            var syntheticId = AniListSeriesIds.FromAniListId(aniListId);

            var existing = _seriesService.FindByTvdbId(syntheticId);
            if (existing != null)
            {
                _logger.Info("Site show '{0}' is already in the library as series {1}", show.Title, existing.Id);
                return existing;
            }

            var resolvedRoot = ResolveRootFolder(rootFolderPath);
            var resolvedProfile = ResolveQualityProfile(qualityProfileId);

            var newSeries = new Series
            {
                TvdbId = syntheticId,
                AniListIds = new HashSet<int> { aniListId },
                QualityProfileId = resolvedProfile,
                RootFolderPath = resolvedRoot,
                SeasonFolder = true,
                Monitored = true,
                MonitorNewItems = NewItemMonitorTypes.All,
                SeriesType = SeriesTypes.Anime,
                AddOptions = new AddSeriesOptions
                {
                    Monitor = MonitorTypes.All,
                    SearchForMissingEpisodes = searchForMissingEpisodes,
                    SearchForCutoffUnmetEpisodes = false
                }
            };

            var added = _addSeriesService.AddSeries(newSeries);
            _logger.Info("Added site show '{0}' as AniList-backed series {1} (anilist:{2})", show.Title, added.Id, aniListId);

            return added;
        }

        private string ResolveRootFolder(string rootFolderPath)
        {
            if (!string.IsNullOrWhiteSpace(rootFolderPath))
            {
                return rootFolderPath;
            }

            var first = _rootFolderService.All().FirstOrDefault();
            if (first == null)
            {
                throw new SiteSeriesAddException("No root folder configured. Add one under Settings > Media Management.");
            }

            return first.Path;
        }

        private int ResolveQualityProfile(int? qualityProfileId)
        {
            if (qualityProfileId is > 0 && _qualityProfileService.Exists(qualityProfileId.Value))
            {
                return qualityProfileId.Value;
            }

            var first = _qualityProfileService.All().FirstOrDefault();
            if (first == null)
            {
                throw new SiteSeriesAddException("No quality profile configured.");
            }

            return first.Id;
        }

        // AnimeSiteIndexer and AnimeSiteImportList are two independent
        // provider instances for "the same site" with nothing formally
        // linking them -- BaseUrl is the only thing they're guaranteed to
        // share, so it's what ties an Import List's catalogue back to the
        // Indexer whose settings know how to turn an episode page into a
        // real download link.
        private AnimeSiteSettings FindMatchingIndexerSettings(string baseUrl)
        {
            var target = (baseUrl ?? string.Empty).TrimEnd('/');

            return _indexerFactory.All()
                .Where(d => d.Implementation == "AnimeSiteIndexer")
                .Select(d => (AnimeSiteSettings)d.Settings)
                .FirstOrDefault(s => string.Equals((s.BaseUrl ?? string.Empty).TrimEnd('/'), target, StringComparison.OrdinalIgnoreCase));
        }

        public void Execute(SiteShowSyncCommand message)
        {
            SyncCatalogue(message.SourceListId);

            // A manual Refresh does a bigger batch and retries every
            // poster-less show; the scheduled run stays small and backs off.
            var manual = message.Trigger == CommandTrigger.Manual;
            BackfillMetadata(message.SourceListId, manual ? 75 : DefaultBackfillLimit, manual);
        }

        // Keeps SiteShows from piling up as orphans once their source
        // import list is removed -- same cleanup ImportListItemService does
        // for Sonarr's own list items.
        public void HandleAsync(ProviderDeletedEvent<IImportList> message)
        {
            _repository.DeleteMany(_repository.FindBySourceList(message.ProviderId));
        }

        private static string SlugFromUrl(string url)
        {
            var trimmed = url.TrimEnd('/');
            var lastSlash = trimmed.LastIndexOf('/');
            return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        }
    }
}
