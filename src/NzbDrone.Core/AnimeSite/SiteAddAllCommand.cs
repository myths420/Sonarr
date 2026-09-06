using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.AnimeSite
{
    // Adds every show in one site's catalogue to the library as a
    // (AniList- or scrape-backed) Series. Triggered by the Sites page
    // "Add All" action.
    public class SiteAddAllCommand : Command
    {
        // The AnimeSite indexer id.
        public int SourceListId { get; set; }

        // Optional overrides; fall back to the first root folder / quality
        // profile when unset (same as the per-show add).
        public string RootFolderPath { get; set; }
        public int? QualityProfileId { get; set; }

        // When true, each added show also kicks off a missing-episode
        // search ("Download All"); when false it is added to the library
        // only ("Add All to Library").
        public bool SearchForMissingEpisodes { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
