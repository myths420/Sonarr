using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.ImportLists.AnimeSite;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.AnimeSite;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource.AniList;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Commands;

namespace NzbDrone.Core.AnimeSite
{
    public interface ISiteShowService
    {
        List<SiteShow> GetForSourceList(int sourceListId);
        SiteShow Get(int id);

        // Browses the site's catalogue script and upserts every show (title
        // and url only). Metadata is filled in by BackfillMetadata.
        int SyncCatalogue(int sourceListId);

        // Looks up metadata for up to `limit` poster-less shows. `force`
        // (manual Refresh) retries every one; a scheduled run backs a failed
        // lookup off for a few days.
        int BackfillMetadata(int sourceListId, int limit, bool force = false);

        // Live episode list for the show detail view (single page fetch).
        List<AnimeSiteEpisodeEntry> GetEpisodes(int showId);

        // Resolves download link(s) for one episode using the indexer's
        // link-resolution settings. Returns an empty list if none resolve.
        List<ResolvedRelease> ResolveEpisodeReleases(int showId, int episodeNumber);

        // Resolves releases for a synthetic Site/AniList-backed Series --
        // maps (season, episode) back to the right catalogue row and its
        // own episode number, then resolves. Used by the indexer's search
        // path so it doesn't have to re-find the show by title.
        List<ResolvedRelease> ResolveReleasesForSeries(Series series, int seasonNumber, int episodeNumber);

        // Adds this catalogue show to the Series tab. AniList-backed when a
        // match exists, otherwise built from the scraped episode list (see
        // AniListSeriesIds / SiteSeriesIds).
        Series AddAsSeries(int showId, string rootFolderPath, int? qualityProfileId, bool searchForMissingEpisodes);
    }

    public class SiteSeriesAddException : Exception
    {
        public SiteSeriesAddException(string message)
            : base(message)
        {
        }
    }

    public class SiteShowService : ISiteShowService, IExecute<SiteShowSyncCommand>, IExecute<SiteAddAllCommand>, IHandleAsync<ProviderDeletedEvent<IIndexer>>
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
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IDiskProvider _diskProvider;
        private readonly IAnimeSiteFetcher _fetcher;
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
                               IManageCommandQueue commandQueueManager,
                               IDiskProvider diskProvider,
                               IAnimeSiteFetcher fetcher,
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
            _commandQueueManager = commandQueueManager;
            _seriesService = seriesService;
            _rootFolderService = rootFolderService;
            _qualityProfileService = qualityProfileService;
            _diskProvider = diskProvider;
            _fetcher = fetcher;
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

            // Pending = no poster, and (force || not tried in the last few days).
            var pending = _repository.FindBySourceList(sourceListId)
                .Where(s => string.IsNullOrEmpty(s.PosterUrl) &&
                            (force || s.LastSyncTime == default || s.LastSyncTime < retryBefore))
                .Take(take)
                .ToList();

            var updated = new List<SiteShow>();
            var aniListHits = 0;
            var scrapeHits = 0;

            var fetch = AnimeSiteFetchOptions.FromSettings(GetIndexerSettings(sourceListId) ?? new AnimeSiteSettings());

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
                    metadata = _scrapeMetadataProvider.ScrapeFromPage(show.Url, fetch);
                    if (metadata != null)
                    {
                        scrapeHits++;
                    }
                }

                if (metadata != null)
                {
                    // Merge non-empty fields only.
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

        public List<ResolvedRelease> ResolveReleasesForSeries(Series series, int seasonNumber, int episodeNumber)
        {
            if (series == null)
            {
                return new List<ResolvedRelease>();
            }

            // Every catalogue row for this show, across ALL configured
            // sites -- the series may have been added from a site whose
            // download host is dead while another site has a working one.
            var rows = CatalogueRowsFor(series, seasonNumber);

            // Prefer sites known to serve mediafire (resolvable, no captcha).
            rows = rows
                .OrderByDescending(r => (r.PosterUrl ?? string.Empty).Contains("animexin"))
                .ThenBy(r => r.Id)
                .ToList();

            foreach (var row in rows)
            {
                var resolved = ResolveEpisodeReleases(row.Id, episodeNumber);
                if (resolved.Count > 0)
                {
                    return resolved;
                }
            }

            _logger.Debug("No site resolved a release for series '{0}' S{1}E{2} ({3} candidate rows)", series.Title, seasonNumber, episodeNumber, rows.Count);
            return new List<ResolvedRelease>();
        }

        private List<SiteShow> CatalogueRowsFor(Series series, int seasonNumber)
        {
            var byId = new Dictionary<int, SiteShow>();

            void Add(SiteShow s)
            {
                if (s != null)
                {
                    byId[s.Id] = s;
                }
            }

            // Exact links: the originating site-show id, and any AniList id
            // the series carries.
            if (SiteSeriesIds.IsSiteId(series.TvdbId))
            {
                Add(_repository.Get(SiteSeriesIds.ToSiteShowId(series.TvdbId)));
            }

            foreach (var aniListId in series.AniListIds)
            {
                Add(_repository.FindByAniListId(aniListId));
            }

            // Same show on other sites: match by cleaned title, honouring
            // the season (a folded season has "Season N" in its row title).
            var baseClean = SeasonTitleParser.Parse(series.Title).BaseTitle.CleanSeriesTitle();
            foreach (var indexer in _indexerFactory.All().Where(d => d.Implementation == "AnimeSiteIndexer"))
            {
                foreach (var s in _repository.FindBySourceList(indexer.Id))
                {
                    var parsed = SeasonTitleParser.Parse(s.Title);
                    if (parsed.BaseTitle.CleanSeriesTitle() == baseClean &&
                        (parsed.Season == seasonNumber || (seasonNumber <= 1 && !parsed.HasSeason)))
                    {
                        Add(s);
                    }
                }
            }

            return byId.Values.ToList();
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
                episodeHtml = _fetcher.GetHtml(episode.Url, show.Url, AnimeSiteFetchOptions.FromSettings(indexerSettings));
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
                // Not backfilled yet; look it up now.
                aniListId = _metadataProvider.Lookup(show.Title)?.AniListId ?? 0;
            }

            // AniList-backed when a match exists, otherwise scrape-backed.
            var syntheticId = aniListId > 0
                ? AniListSeriesIds.FromAniListId(aniListId)
                : SiteSeriesIds.FromSiteShowId(show.Id);

            // Idempotent across both id schemes and by cleaned title.
            var existing = FindExistingSeries(show, aniListId, syntheticId);
            if (existing != null)
            {
                _logger.Info("Site show '{0}' is already in the library as series {1}", show.Title, existing.Id);
                return existing;
            }

            // "<Show> Season 2/3/..." -> fold into the base show rather than
            // adding a poster of its own. Match the base show by cleaned
            // title (its own, or with a season suffix stripped).
            var seasonInfo = SeasonTitleParser.Parse(show.Title);
            if (seasonInfo.HasSeason)
            {
                var baseClean = seasonInfo.BaseTitle.CleanSeriesTitle();
                var baseSeries = _seriesService.GetAllSeries().FirstOrDefault(s =>
                    s.CleanTitle == baseClean ||
                    SeasonTitleParser.Parse(s.Title).BaseTitle.CleanSeriesTitle() == baseClean);

                if (baseSeries != null)
                {
                    // AniList-backed base: record this season's id and refresh
                    // so the proxy rebuilds Season 1..N. A TheTVDB base already
                    // has all its seasons -- nothing to merge, just reuse it.
                    if (aniListId > 0 && AniListSeriesIds.IsAniListId(baseSeries.TvdbId) && !baseSeries.AniListIds.Contains(aniListId))
                    {
                        baseSeries.AniListIds.Add(aniListId);
                        _seriesService.UpdateSeries(baseSeries, publishUpdatedEvent: false);
                        _commandQueueManager.Push(new RefreshSeriesCommand(new List<int> { baseSeries.Id }));
                    }

                    _logger.Info("Folded site show '{0}' into series {1} '{2}' as season {3}", show.Title, baseSeries.Id, baseSeries.Title, seasonInfo.Season);
                    return baseSeries;
                }
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

        // If a differently-cased folder for this show already exists on
        // disk, point the series at that exact folder.
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

            // Cleaned-title match, so a hand-added / TheTVDB series for the
            // same show is reused rather than duplicated.
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

        // SiteShow.SourceListId holds an AnimeSite indexer id. Null if the
        // indexer has been deleted.
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

            var manual = message.Trigger == CommandTrigger.Manual;
            BackfillMetadata(message.SourceListId, manual ? 75 : DefaultBackfillLimit, manual);
        }

        public void Execute(SiteAddAllCommand message)
        {
            var shows = _repository.FindBySourceList(message.SourceListId);
            if (shows.Count == 0)
            {
                _logger.Warn("Add All: no catalogue shows for indexer {0} -- Refresh it first.", message.SourceListId);
                return;
            }

            var added = 0;
            var skipped = 0;
            var failed = 0;
            var seriesIds = new HashSet<int>();

            for (var i = 0; i < shows.Count; i++)
            {
                var show = shows[i];
                _logger.ProgressInfo("Add All: {0}/{1} - {2}", i + 1, shows.Count, show.Title);

                try
                {
                    // Search is kicked off once per series below so shows
                    // already in the library still get a grab.
                    var series = AddAsSeries(show.Id, message.RootFolderPath, message.QualityProfileId, false);
                    if (series != null)
                    {
                        added++;
                        seriesIds.Add(series.Id);
                    }
                }
                catch (SiteSeriesAddException ex)
                {
                    skipped++;
                    _logger.Debug("Add All: skipped '{0}': {1}", show.Title, ex.Message);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.Warn(ex, "Add All: failed to add '{0}'", show.Title);
                }
            }

            _logger.Info("Add All for indexer {0}: {1} added, {2} skipped (no metadata / already present), {3} failed", message.SourceListId, added, skipped, failed);

            if (message.SearchForMissingEpisodes && seriesIds.Count > 0)
            {
                _logger.Info("Download All: queuing episode search for {0} series", seriesIds.Count);
                foreach (var seriesId in seriesIds)
                {
                    _commandQueueManager.Push(new SeriesSearchCommand(seriesId));
                }
            }
        }

        // Drop a site's catalogue rows when its indexer is deleted.
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
