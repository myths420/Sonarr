using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
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

    public class SiteShowService : ISiteShowService, IExecute<SiteShowSyncCommand>, IHandleAsync<ProviderDeletedEvent<IIndexer>>
    {
        private const int DefaultBackfillLimit = 25;

        private readonly ISiteShowRepository _repository;
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
        private readonly IDiskProvider _diskProvider;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public SiteShowService(ISiteShowRepository repository,
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
                               IDiskProvider diskProvider,
                               IHttpClient httpClient,
                               Logger logger)
        {
            _repository = repository;
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
            _diskProvider = diskProvider;
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
            var options = GetCatalogueOptions(sourceListId);
            if (options == null)
            {
                _logger.Warn("No AnimeSite indexer {0} -- can't sync its Sites catalogue", sourceListId);
                return 0;
            }

            var entries = _catalogBrowser.Browse(options, _logger);
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
            var options = GetCatalogueOptions(show.SourceListId);

            return options == null
                ? new List<AnimeSiteEpisodeEntry>()
                : _catalogBrowser.BrowseEpisodes(options, show.Url, _logger);
        }

        public List<ResolvedRelease> ResolveEpisodeReleases(int showId, int episodeNumber)
        {
            var show = _repository.Get(showId);

            var indexerSettings = GetIndexerSettings(show.SourceListId);
            if (indexerSettings == null)
            {
                _logger.Warn("AnimeSite indexer {0} for this catalogue no longer exists -- can't resolve downloads.", show.SourceListId);
                return new List<ResolvedRelease>();
            }

            var episodes = _catalogBrowser.BrowseEpisodes(AnimeSiteCatalogueOptions.FromIndexer(indexerSettings), show.Url, _logger);
            var episode = episodes.FirstOrDefault(e => e.Number == episodeNumber);
            if (episode == null)
            {
                _logger.Debug("Episode {0} not found for {1}", episodeNumber, show.Title);
                return new List<ResolvedRelease>();
            }

            string episodeHtml;
            try
            {
                episodeHtml = _httpClient.Get(AnimeSiteHttp.BuildRequest(episode.Url, show.Url)).Content;
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

            // Prefer an AniList-backed series (real air dates, richer
            // metadata); otherwise fall back to a series built from the
            // site's own title + scraped episode list, so downloading from
            // *any* catalogue show still lands it in the Series tab.
            var syntheticId = aniListId > 0
                ? AniListSeriesIds.FromAniListId(aniListId)
                : SiteSeriesIds.FromSiteShowId(show.Id);

            // Never add a second series for the same show. Check both
            // synthetic id schemes (a show first added scrape-backed, then
            // matched on AniList later, must resolve to the same series),
            // the AniList id itself, and -- as a backstop against the
            // add-from-folder screen -- an existing series folder.
            var existing = FindExistingSeries(show, aniListId, syntheticId);
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
                AniListIds = aniListId > 0 ? new HashSet<int> { aniListId } : new HashSet<int>(),
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
            var backing = aniListId > 0 ? $"anilist:{aniListId}" : $"site-scrape:{show.Id}";
            _logger.Info("Added site show '{0}' as series {1} ({2})", show.Title, added.Id, backing);

            AdoptExistingFolderCasing(added);

            return added;
        }

        // If the show was downloaded before (files already on disk under a
        // differently-cased folder), point the new series at that exact
        // folder. Otherwise Sonarr -- case-sensitive on Linux even when the
        // volume isn't -- treats the real folder as unmapped and the "add
        // shows already downloaded" screen offers it again, ending up with
        // two series over one folder.
        private void AdoptExistingFolderCasing(Series series)
        {
            if (string.IsNullOrWhiteSpace(series.Path))
            {
                return;
            }

            var parent = System.IO.Path.GetDirectoryName(series.Path);
            var name = System.IO.Path.GetFileName(series.Path);
            if (string.IsNullOrEmpty(parent) || !_diskProvider.FolderExists(parent))
            {
                return;
            }

            var match = _diskProvider.GetDirectories(parent)
                .FirstOrDefault(d => string.Equals(System.IO.Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase) &&
                                     !string.Equals(System.IO.Path.GetFileName(d), name, StringComparison.Ordinal));

            if (match == null)
            {
                return;
            }

            _logger.Info("Pointing series '{0}' at existing folder {1} (was {2})", series.Title, match, series.Path);
            series.Path = match;
            _seriesService.UpdateSeries(series);
        }

        private Series FindExistingSeries(SiteShow show, int aniListId, int syntheticId)
        {
            var all = _seriesService.GetAllSeries();

            var byId = all.FirstOrDefault(s =>
                s.TvdbId == syntheticId ||
                s.TvdbId == SiteSeriesIds.FromSiteShowId(show.Id) ||
                (aniListId > 0 && (s.TvdbId == AniListSeriesIds.FromAniListId(aniListId) || s.AniListIds.Contains(aniListId))));

            if (byId != null)
            {
                return byId;
            }

            // Case-insensitive title match against an existing series folder
            // name -- guards against a duplicate created via the "add shows
            // already downloaded" screen when its folder casing differs.
            var slug = Parser.Parser.CleanSeriesTitle(show.Title ?? string.Empty);
            return string.IsNullOrEmpty(slug)
                ? null
                : all.FirstOrDefault(s => s.CleanTitle == slug);
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

        // A "site" in the Sites section is exactly one AnimeSite indexer --
        // SiteShow.SourceListId holds that indexer's id. Returns null if the
        // indexer has since been deleted (its catalogue rows get cleaned up
        // by the ProviderDeletedEvent handler below).
        private AnimeSiteSettings GetIndexerSettings(int indexerId)
        {
            var definition = _indexerFactory.All()
                .FirstOrDefault(d => d.Id == indexerId && d.Implementation == "AnimeSiteIndexer");

            return definition?.Settings as AnimeSiteSettings;
        }

        private AnimeSiteCatalogueOptions GetCatalogueOptions(int indexerId)
        {
            var settings = GetIndexerSettings(indexerId);
            return settings == null ? null : AnimeSiteCatalogueOptions.FromIndexer(settings);
        }

        public void Execute(SiteShowSyncCommand message)
        {
            SyncCatalogue(message.SourceListId);

            // A manual Refresh does a bigger batch and retries every
            // poster-less show; the scheduled run stays small and backs off.
            var manual = message.Trigger == CommandTrigger.Manual;
            BackfillMetadata(message.SourceListId, manual ? 75 : DefaultBackfillLimit, manual);
        }

        // Drop a site's whole catalogue when its AnimeSite indexer is
        // deleted, so nothing lingers under Sites.
        public void HandleAsync(ProviderDeletedEvent<IIndexer> message)
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
