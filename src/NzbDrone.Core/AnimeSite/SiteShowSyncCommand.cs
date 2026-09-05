using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.AnimeSite
{
    // Triggered from the Sites catalogue page (POST /api/v3/command with
    // name "SiteShowSync") -- runs on the background command queue so a
    // few-hundred-show site doesn't block the request that kicked it off.
    public class SiteShowSyncCommand : Command
    {
        public int SourceListId { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
