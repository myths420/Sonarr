using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.AnimeSite
{
    // Triggered by the Sites page Refresh action for one site (SourceListId
    // set): full catalogue + metadata + episode-cache sync. Also runs on a
    // schedule with no SourceListId: a light pass that only refreshes the
    // scraped episode caches so newly-aired episodes get picked up.
    public class SiteShowSyncCommand : Command
    {
        // The AnimeSite indexer id. 0 = every AnimeSite indexer.
        public int SourceListId { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
