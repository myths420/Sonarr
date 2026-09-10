using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.ImportLists.AnimeSite;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.AnimeSite;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
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

        // Pins this catalogue row to a library series (and one of its
        // seasons) by hand. seriesId <= 0 clears the link. season null falls
        // back to the season parsed from the row title.
        SiteShow SetManualLink(int showId, int seriesId, int? season);

        // Deletes this show's stream-sourced episode files (WEB-DL, from the
        // Dailymotion / Rumble path) and re-downloads them with the current
        // resolver. webOnly=false wipes every file for the show. Used to
        // replace files from the pre-fix remux that play badly.
        SiteRepairResult RepairShow(int showId, bool webOnly, bool forceRedownload = false);
    }

    public class SiteRepairResult
    {
        // Files fixed by an in-place re-mux (no re-download needed).
        public int Repaired { get; set; }

        // Files that couldn't be re-muxed -- deleted and re-downloaded.
        public int Redownloaded { get; set; }
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
        private const int EpisodeCacheBatch = 40;

        private static readonly TimeSpan EpisodeCacheTtl = TimeSpan.FromHours(18);

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
        private readonly Lazy<ISiteDownloadService> _siteDownloadService;
        private readonly IEpisodeService _episodeService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IDeleteMediaFiles _mediaFileDeletionService;
        private readonly ISiteFileRepairService _fileRepairService;
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
                               Lazy<ISiteDownloadService> siteDownloadService,
                               IEpisodeService episodeService,
                               IMediaFileService mediaFileService,
                               IDeleteMediaFiles mediaFileDeletionService,
                               ISiteFileRepairService fileRepairService,
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
            _mediaFileDeletionService = mediaFileDeletionService;
            _fetcher = fetcher;
            _siteDownloadService = siteDownloadService;
            _episodeService = episodeService;
            _mediaFileService = mediaFileService;
            _fileRepairService = fileRepairService;
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

        public SiteShow SetManualLink(int showId, int seriesId, int? season)
        {
            var show = _repository.Get(showId);
            if (show == null)
            {
                throw new SiteSeriesAddException("Site show not found.");
            }

            if (seriesId <= 0)
            {
                show.MappedSeriesId = 0;
                show.MappedSeason = 0;
                _repository.Update(show);
                _logger.Info("Cleared the manual library link on site show '{0}'", show.Title);
                return show;
            }

            var series = _seriesService.GetAllSeries().FirstOrDefault(s => s.Id == seriesId)
                         ?? throw new SiteSeriesAddException($"Series {seriesId} not found.");

            // The series this row resolved to before the manual link -- its
            // own scrape-backed / AniList-backed series, if it has one.
            var previous = AutoResolvedSeries(show);

            show.MappedSeriesId = series.Id;
            show.MappedSeason = season is > 0 ? season.Value : SeasonTitleParser.Parse(show.Title).Season;
            _repository.Update(show);

            _logger.Info("Linked site show '{0}' to series {1} '{2}' as season {3}", show.Title, series.Id, series.Title, show.MappedSeason);

            if (previous != null && previous.Id != series.Id)
            {
                MoveFilesToLinkedSeries(previous, series, show.MappedSeason);
            }

            // Refresh so the linked series picks up the mapped season's
            // episodes (SkyHookProxy.AppendMappedRowEpisodes), then rescan so
            // the moved files import against them.
            _commandQueueManager.Push(new RefreshSeriesCommand(new List<int> { series.Id }));
            _commandQueueManager.Push(new RescanSeriesCommand(series.Id));

            return show;
        }

        // The library series a row resolves to with no manual link -- its own
        // synthetic series, if it has one.
        private Series AutoResolvedSeries(SiteShow show)
        {
            return _seriesService.GetAllSeries().FirstOrDefault(s =>
                s.TvdbId == SiteSeriesIds.FromSiteShowId(show.Id) ||
                (show.AniListId > 0 && (s.TvdbId == AniListSeriesIds.FromAniListId(show.AniListId) || s.AniListIds.Contains(show.AniListId))));
        }

        public SiteRepairResult RepairShow(int showId, bool webOnly, bool forceRedownload = false)
        {
            var show = _repository.Get(showId);
            if (show == null)
            {
                throw new SiteSeriesAddException("Site show not found.");
            }

            var series = show.MappedSeriesId > 0
                ? _seriesService.GetAllSeries().FirstOrDefault(s => s.Id == show.MappedSeriesId)
                : AutoResolvedSeries(show);

            var result = new SiteRepairResult();
            if (series == null)
            {
                _logger.Debug("Sites repair: '{0}' isn't in the library yet", show.Title);
                return result;
            }

            var season = show.MappedSeason > 0 ? show.MappedSeason : SeasonTitleParser.Parse(show.Title).Season;
            var episodes = _episodeService.GetEpisodeBySeries(series.Id);
            var episodeByFileId = episodes.Where(e => e.EpisodeFileId > 0).ToDictionary(e => e.EpisodeFileId);
            var hasSeason = episodes.Any(e => e.SeasonNumber == season);

            foreach (var file in _mediaFileService.GetFilesBySeries(series.Id))
            {
                if (!episodeByFileId.TryGetValue(file.Id, out var episode))
                {
                    continue;
                }

                if (hasSeason && episode.SeasonNumber != season)
                {
                    continue;
                }

                var isWeb = (file.Quality?.Quality?.Name ?? string.Empty).Contains("WEB", StringComparison.OrdinalIgnoreCase);
                if (webOnly && !isWeb)
                {
                    continue;
                }

                var path = Path.Combine(series.Path, file.RelativePath);

                if (!forceRedownload && _fileRepairService.TryRepairInPlace(path))
                {
                    result.Repaired++;
                    continue;
                }

                // Couldn't re-mux it (truncated / unreadable) -- delete and
                // grab it again with the current resolver.
                try
                {
                    _mediaFileDeletionService.DeleteEpisodeFile(series, file);
                    if (_siteDownloadService.Value.StartDownload(showId, episode.EpisodeNumber, releaseUrl: null, skipIfPresent: false) != null)
                    {
                        result.Redownloaded++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Sites repair: couldn't replace '{0}'", path);
                }
            }

            _logger.Info("Sites repair '{0}': re-muxed {1}, re-downloaded {2}", show.Title, result.Repaired, result.Redownloaded);
            if (result.Repaired > 0 || result.Redownloaded > 0)
            {
                _commandQueueManager.Push(new RescanSeriesCommand(series.Id));
            }

            return result;
        }

        // On a manual link, move the episode files from the row's old
        // stand-alone series into the linked series' season folder (renamed
        // S{season}E{ep}), then rescan both so the linked series imports
        // them and the old one drops the now-missing files.
        private void MoveFilesToLinkedSeries(Series from, Series to, int toSeason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(from.Path) || string.IsNullOrWhiteSpace(to.Path) || !_diskProvider.FolderExists(to.Path))
                {
                    return;
                }

                var files = _mediaFileService.GetFilesBySeries(from.Id);
                if (files.Count == 0)
                {
                    return;
                }

                var seasonFolder = to.SeasonFolder ? Path.Combine(to.Path, $"Season {toSeason:00}") : to.Path;
                _diskProvider.EnsureFolder(seasonFolder);

                var moved = 0;
                foreach (var file in files)
                {
                    var src = file.Path;
                    if (string.IsNullOrEmpty(src) || !_diskProvider.FileExists(src))
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(src);
                    int episodeNumber;

                    var se = Regex.Match(name, @"[Ss](\d{1,3})[Ee](\d{1,3})");
                    if (se.Success)
                    {
                        episodeNumber = int.Parse(se.Groups[2].Value);
                    }
                    else
                    {
                        var epOnly = Regex.Match(name, @"Episode\s+(\d{1,4})");
                        if (!epOnly.Success)
                        {
                            continue;
                        }

                        episodeNumber = int.Parse(epOnly.Groups[1].Value);
                    }

                    var quality = file.Quality?.Quality?.Name;
                    if (string.IsNullOrWhiteSpace(quality) || quality == "Unknown")
                    {
                        quality = "HDTV-1080p";
                    }

                    var destName = FileNameSafe($"{to.Title} - S{toSeason:00}E{episodeNumber:00} - Episode {episodeNumber} [{quality}]") + Path.GetExtension(src);
                    var dest = Path.Combine(seasonFolder, destName);

                    if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (_diskProvider.FileExists(dest))
                    {
                        _diskProvider.DeleteFile(dest);
                    }

                    _diskProvider.MoveFile(src, dest);
                    moved++;
                }

                _logger.Info("Manual link: moved {0} file(s) from '{1}' into '{2}' Season {3}", moved, from.Title, to.Title, toSeason);

                if (moved > 0)
                {
                    _commandQueueManager.Push(new RescanSeriesCommand(from.Id));
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Manual link: couldn't move files from '{0}' to '{1}'", from.Title, to.Title);
            }
        }

        private static string FileNameSafe(string value)
        {
            return string.Join("_", (value ?? string.Empty).Split(Path.GetInvalidFileNameChars())).Trim();
        }

        public Series AddAsSeries(int showId, string rootFolderPath, int? qualityProfileId, bool searchForMissingEpisodes)
        {
            var show = _repository.Get(showId);
            if (show == null)
            {
                throw new SiteSeriesAddException("Site show not found.");
            }

            if (show.MappedSeriesId > 0)
            {
                var mapped = _seriesService.GetAllSeries().FirstOrDefault(s => s.Id == show.MappedSeriesId);
                if (mapped != null)
                {
                    return mapped;
                }
            }

            var aniListId = show.AniListId;
            if (aniListId <= 0)
            {
                // Not backfilled yet; look it up now -- try the full title,
                // then the season-stripped base (AniList search does badly
                // with "... Season 3" / typo'd suffixes).
                aniListId = _metadataProvider.Lookup(show.Title)?.AniListId
                    ?? _metadataProvider.Lookup(SeasonTitleParser.Parse(show.Title).BaseTitle)?.AniListId
                    ?? 0;
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
            _logger.Debug("AddAsSeries '{0}': base '{1}', season {2}, hasSeason {3}, aniList {4}", show.Title, seasonInfo.BaseTitle, seasonInfo.Season, seasonInfo.HasSeason, aniListId);

            if (seasonInfo.HasSeason)
            {
                var baseClean = seasonInfo.BaseTitle.CleanSeriesTitle();
                var baseKey = SiteTitleMatch.Key(seasonInfo.BaseTitle);
                var baseSeries = _seriesService.GetAllSeries().FirstOrDefault(s =>
                    s.CleanTitle == baseClean ||
                    SeasonTitleParser.Parse(s.Title).BaseTitle.CleanSeriesTitle() == baseClean ||
                    (baseKey.Length > 0 && SiteTitleMatch.Key(s.Title) == baseKey));

                if (baseSeries == null)
                {
                    _logger.Debug("AddAsSeries '{0}': no existing series matched base clean-title '{1}' -- adding as its own series", show.Title, baseClean);
                }
                else
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
            // same show -- or the same show added from another site under a
            // slightly different name (trailing year, "New", sub tags) -- is
            // reused rather than duplicated. Higher seasons are left to the
            // caller's fold step, which records the season's AniList id.
            var slug = Parser.Parser.CleanSeriesTitle(show.Title ?? string.Empty);
            var byClean = string.IsNullOrEmpty(slug) ? null : all.FirstOrDefault(s => s.CleanTitle == slug);
            if (byClean != null)
            {
                return byClean;
            }

            if (SeasonTitleParser.Parse(show.Title).HasSeason)
            {
                return null;
            }

            var key = SiteTitleMatch.Key(show.Title);
            return key.Length > 2
                ? all.FirstOrDefault(s => SiteTitleMatch.Key(s.Title) == key)
                : null;
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
            var indexerIds = message.SourceListId > 0
                ? new List<int> { message.SourceListId }
                : _indexerFactory.All().Where(d => d.Implementation == "AnimeSiteIndexer").Select(d => d.Id).ToList();

            // Scheduled run: just keep the episode caches fresh (cheap, one
            // page per stale show). The full catalogue re-sync + metadata
            // backfill stay on the Sites page Refresh button.
            if (message.Trigger == CommandTrigger.Scheduled)
            {
                foreach (var id in indexerIds)
                {
                    RefreshEpisodeCache(id, force: false);
                }

                return;
            }

            var manual = message.Trigger == CommandTrigger.Manual;

            foreach (var id in indexerIds)
            {
                SyncCatalogue(id);
                BackfillMetadata(id, manual ? 75 : DefaultBackfillLimit, manual);
                RefreshEpisodeCache(id, force: manual);
            }

            // Shows added while AniList was unreachable are scrape-backed
            // and don't fold. Retry the AniList lookup and migrate any
            // that now match.
            UpgradeScrapeBackedSeries();

            // Rescan the folders of every Site/AniList-backed series
            // (regardless of which catalogue triggered this) so counters
            // reflect what's on disk and loose files get imported.
            RescanSyntheticSeries();
        }

        // Refresh the cached scraped episode list (number + title + parsed
        // air date) for shows whose cache is missing or older than the TTL.
        // A scheduled run only does a batch so it cycles through the
        // catalogue over a day or two; a manual Refresh does the lot.
        private void RefreshEpisodeCache(int sourceListId, bool force)
        {
            var options = GetCatalogueOptions(sourceListId);
            if (options == null)
            {
                return;
            }

            var now = DateTime.UtcNow;

            var stale = _repository.FindBySourceList(sourceListId)
                .Where(s => force || s.LastEpisodeSync == default || now - s.LastEpisodeSync > EpisodeCacheTtl)
                .OrderBy(s => s.LastEpisodeSync)
                .Take(force ? int.MaxValue : EpisodeCacheBatch)
                .ToList();

            if (stale.Count == 0)
            {
                return;
            }

            var updated = new List<SiteShow>();

            foreach (var show in stale)
            {
                try
                {
                    var episodes = _catalogBrowser.BrowseEpisodes(options, show.Url, _logger)
                        .Where(e => e.Number > 0)
                        .GroupBy(e => e.Number)
                        .Select(g => g.First())
                        .OrderBy(e => e.Number)
                        .Select(e => new SiteShowEpisode
                        {
                            Number = e.Number,
                            Title = e.Title,
                            AirDateUtc = SiteEpisodeTitle.ParseAirDate(e.Title)
                        })
                        .ToList();

                    if (episodes.Count == 0)
                    {
                        continue;
                    }

                    show.SetCachedEpisodes(episodes);
                    if (episodes.Count > show.Episodes)
                    {
                        show.Episodes = episodes.Count;
                    }

                    updated.Add(show);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Sites sync: couldn't refresh the episode cache for '{0}'", show.Title);
                }
            }

            if (updated.Count > 0)
            {
                _repository.UpdateMany(updated);
                _logger.Info("Sites sync: refreshed the episode cache for {0} show(s) on indexer {1}", updated.Count, sourceListId);
            }
        }

        private void UpgradeScrapeBackedSeries()
        {
            List<Series> scrapeBacked;
            try
            {
                scrapeBacked = _seriesService.GetAllSeries()
                    .Where(s => SiteSeriesIds.IsSiteId(s.TvdbId))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Metadata upgrade: couldn't list scrape-backed series");
                return;
            }

            if (scrapeBacked.Count == 0)
            {
                return;
            }

            var upgraded = 0;
            foreach (var series in scrapeBacked)
            {
                try
                {
                    var show = _repository.Get(SiteSeriesIds.ToSiteShowId(series.TvdbId));
                    if (show == null)
                    {
                        continue;
                    }

                    var baseTitle = SeasonTitleParser.Parse(show.Title).BaseTitle;
                    var aniListId = _metadataProvider.Lookup(show.Title)?.AniListId
                        ?? _metadataProvider.Lookup(baseTitle)?.AniListId
                        ?? 0;

                    if (aniListId <= 0)
                    {
                        continue;
                    }

                    var existingAniList = _seriesService.GetAllSeries().FirstOrDefault(s =>
                        s.Id != series.Id &&
                        (s.TvdbId == AniListSeriesIds.FromAniListId(aniListId) || s.AniListIds.Contains(aniListId)));

                    if (existingAniList != null)
                    {
                        _logger.Warn("Metadata upgrade: '{0}' (series {1}) matches AniList {2} but series {3} '{4}' already holds it -- move its files there and delete the duplicate.", show.Title, series.Id, aniListId, existingAniList.Id, existingAniList.Title);
                        continue;
                    }

                    _logger.Info("Metadata upgrade: migrating scrape-backed series {0} '{1}' to AniList {2}", series.Id, series.Title, aniListId);
                    series.TvdbId = AniListSeriesIds.FromAniListId(aniListId);
                    series.AniListIds.Add(aniListId);
                    _seriesService.UpdateSeries(series, publishUpdatedEvent: false);
                    _commandQueueManager.Push(new RefreshSeriesCommand(new List<int> { series.Id }));
                    upgraded++;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Metadata upgrade: failed for series {0}", series.Id);
                }
            }

            if (upgraded > 0)
            {
                _logger.Info("Metadata upgrade: migrated {0} scrape-backed series to AniList", upgraded);
            }
        }

        private void RescanSyntheticSeries()
        {
            try
            {
                var series = _seriesService.GetAllSeries()
                    .Where(s => AniListSeriesIds.IsAniListId(s.TvdbId) || SiteSeriesIds.IsSiteId(s.TvdbId))
                    .ToList();

                if (series.Count == 0)
                {
                    return;
                }

                _logger.Debug("Sites refresh: refreshing + rescanning {0} Site/AniList series", series.Count);

                // Refresh first (rebuilds the episode list from metadata),
                // then rescan (imports files against those episodes).
                _commandQueueManager.Push(new RefreshSeriesCommand(series.Select(s => s.Id).ToList()));

                foreach (var s in series)
                {
                    RemoveDuplicateRootFiles(s);
                    _commandQueueManager.Push(new RescanSeriesCommand(s.Id));
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Sites refresh: couldn't rescan series");
            }
        }

        // Sites downloads used to land loose in the series root instead of
        // the season folder. Delete a loose root video file when its
        // episode is already imported from a file inside a subfolder --
        // leave the ones that are still the only copy.
        private void RemoveDuplicateRootFiles(Series series)
        {
            try
            {
                if (series == null || string.IsNullOrWhiteSpace(series.Path) || !_diskProvider.FolderExists(series.Path))
                {
                    return;
                }

                var rootFiles = _diskProvider.GetFiles(series.Path, false)
                    .Where(f => new[] { ".mp4", ".mkv", ".avi", ".m4v" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .ToList();

                if (rootFiles.Count == 0)
                {
                    return;
                }

                var filesBySeries = _mediaFileService.GetFilesBySeries(series.Id);

                foreach (var loose in rootFiles)
                {
                    var name = Path.GetFileNameWithoutExtension(loose);
                    var m = Regex.Match(name, @"[Ss](\d{1,2})[Ee](\d{1,3})");
                    if (!m.Success)
                    {
                        continue;
                    }

                    var season = int.Parse(m.Groups[1].Value);
                    var number = int.Parse(m.Groups[2].Value);

                    var episode = _episodeService.FindEpisode(series.Id, season, number);
                    if (episode is not { EpisodeFileId: > 0 })
                    {
                        continue;
                    }

                    var importedFile = filesBySeries.FirstOrDefault(f => f.Id == episode.EpisodeFileId);
                    var importedPath = importedFile?.Path;

                    // Only delete when the tracked file is a *different*,
                    // subfolder file -- never the loose one itself.
                    if (string.IsNullOrEmpty(importedPath) ||
                        string.Equals(Path.GetFullPath(importedPath), Path.GetFullPath(loose), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetDirectoryName(importedPath), series.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _logger.Info("Sites refresh: deleting duplicate loose file '{0}' (S{1:00}E{2:00} already imported to '{3}')", loose, season, number, importedPath);
                    _diskProvider.DeleteFile(loose);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Sites refresh: duplicate cleanup failed for '{0}'", series?.Title);
            }
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
            var queued = 0;

            var download = message.SearchForMissingEpisodes;
            var verb = download ? "Download All" : "Add All";

            for (var i = 0; i < shows.Count; i++)
            {
                var show = shows[i];
                _logger.ProgressInfo("{0}: {1}/{2} - {3}", verb, i + 1, shows.Count, show.Title);

                // AddAsSeries returns the existing series when already
                // present, so this stays idempotent. A failure here (e.g.
                // AniList metadata unavailable) must not stop the download
                // loop -- StartDownload auto-creates the series anyway.
                try
                {
                    if (AddAsSeries(show.Id, message.RootFolderPath, message.QualityProfileId, false) != null)
                    {
                        added++;
                    }
                }
                catch (SiteSeriesAddException ex)
                {
                    skipped++;
                    _logger.Debug("{0}: '{1}' not added to the library: {2}", verb, show.Title, ex.Message);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.Warn(ex, "{0}: add failed for '{1}'", verb, show.Title);
                }

                if (!download)
                {
                    continue;
                }

                // Same path as clicking Download on a poster: pull each
                // episode straight from this site's downloader, no indexer
                // search / download client involved.
                try
                {
                    foreach (var episode in GetEpisodes(show.Id))
                    {
                        // Skip episodes already in the library unless the
                        // site has a higher-quality release.
                        if (_siteDownloadService.Value.StartDownload(show.Id, episode.Number, releaseUrl: null, skipIfPresent: true) != null)
                        {
                            queued++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.Warn(ex, "{0}: episode download failed for '{1}'", verb, show.Title);
                }
            }

            _logger.Info("{0} for indexer {1}: {2} added, {3} skipped, {4} failed, {5} episode download(s) queued", verb, message.SourceListId, added, skipped, failed, queued);
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
