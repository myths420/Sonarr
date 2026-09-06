using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.AnimeSite
{
    // Syncs one site's catalogue. Triggered by the Sites page Refresh action.
    public class SiteShowSyncCommand : Command
    {
        // The AnimeSite indexer id.
        public int SourceListId { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
