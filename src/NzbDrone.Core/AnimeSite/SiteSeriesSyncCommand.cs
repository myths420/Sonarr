using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.AnimeSite
{
    // Scheduled: refreshes Site/AniList-backed series so newly-aired
    // episodes appear, then searches for any monitored episode that has
    // aired but isn't on disk.
    public class SiteSeriesSyncCommand : Command
    {
        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => false;
    }
}
