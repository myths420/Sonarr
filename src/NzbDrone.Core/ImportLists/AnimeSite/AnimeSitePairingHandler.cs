using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.AnimeSite;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.ImportLists.AnimeSite
{
    // An AnimeSite "site" needs two provider instances that share a BaseUrl:
    // the indexer (search + link-resolution settings) and the import list
    // (the browsable catalogue behind the Sites section). Adding just the
    // indexer used to leave nothing under Sites. This pairs them up: when an
    // AnimeSite indexer is saved and no AnimeSite import list already covers
    // its site, one is auto-created (automatic-add off -- it exists purely
    // so the Sites catalogue has something to browse).
    public class AnimeSitePairingHandler : IHandle<ProviderAddedEvent<IIndexer>>,
                                           IHandle<ProviderUpdatedEvent<IIndexer>>,
                                           IHandle<ApplicationStartedEvent>
    {
        private const string IndexerImplementation = "AnimeSiteIndexer";
        private const string ImportListImplementation = "AnimeSiteImportList";

        private readonly IIndexerFactory _indexerFactory;
        private readonly IImportListFactory _importListFactory;
        private readonly IRootFolderService _rootFolderService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly Logger _logger;

        public AnimeSitePairingHandler(IIndexerFactory indexerFactory,
                                       IImportListFactory importListFactory,
                                       IRootFolderService rootFolderService,
                                       IQualityProfileService qualityProfileService,
                                       Logger logger)
        {
            _indexerFactory = indexerFactory;
            _importListFactory = importListFactory;
            _rootFolderService = rootFolderService;
            _qualityProfileService = qualityProfileService;
            _logger = logger;
        }

        public void Handle(ProviderAddedEvent<IIndexer> message)
        {
            EnsurePairedImportList(message.Definition);
        }

        public void Handle(ProviderUpdatedEvent<IIndexer> message)
        {
            EnsurePairedImportList(message.Definition);
        }

        // Pick up AnimeSite indexers that were added before this handler
        // existed (or whose paired list was deleted).
        public void Handle(ApplicationStartedEvent message)
        {
            foreach (var indexer in _indexerFactory.All().Where(d => d.Implementation == IndexerImplementation))
            {
                EnsurePairedImportList(indexer);
            }
        }

        private void EnsurePairedImportList(ProviderDefinition indexerDefinition)
        {
            if (indexerDefinition?.Implementation != IndexerImplementation ||
                indexerDefinition.Settings is not AnimeSiteSettings indexerSettings)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(NormalizeUrl(indexerSettings.BaseUrl)))
            {
                return;
            }

            // One Sites entry per configured site name -- so an indexer named
            // "test" gets a "test" catalogue even if another list already
            // covers the same URL. Match by name (case-insensitive).
            var alreadyPaired = _importListFactory.All()
                .Any(d => d.Implementation == ImportListImplementation &&
                          string.Equals(d.Name, indexerDefinition.Name, StringComparison.OrdinalIgnoreCase));

            if (alreadyPaired)
            {
                return;
            }

            var rootFolder = _rootFolderService.All().FirstOrDefault();
            if (rootFolder == null)
            {
                _logger.Warn("AnimeSite indexer '{0}' saved but no root folder is configured, so its Sites catalogue wasn't set up. Add a root folder and re-save the indexer.", indexerDefinition.Name);
                return;
            }

            var qualityProfile = _qualityProfileService.All().FirstOrDefault();
            if (qualityProfile == null)
            {
                return;
            }

            var listDefinition = new ImportListDefinition
            {
                Name = indexerDefinition.Name,
                Implementation = ImportListImplementation,
                ConfigContract = nameof(AnimeSiteImportListSettings),
                Settings = new AnimeSiteImportListSettings { BaseUrl = indexerSettings.BaseUrl },
                EnableAutomaticAdd = false,
                SearchForMissingEpisodes = false,
                ShouldMonitor = MonitorTypes.All,
                MonitorNewItems = NewItemMonitorTypes.All,
                SeriesType = SeriesTypes.Anime,
                SeasonFolder = true,
                QualityProfileId = qualityProfile.Id,
                RootFolderPath = rootFolder.Path
            };

            _importListFactory.Create(listDefinition);
            _logger.Info("Created Sites catalogue '{0}' for AnimeSite indexer ({1})", indexerDefinition.Name, indexerSettings.BaseUrl);
        }

        private static string NormalizeUrl(string url)
        {
            return (url ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
        }
    }
}
