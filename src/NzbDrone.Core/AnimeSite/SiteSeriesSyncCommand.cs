using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.AnimeSite
{
    // Scheduled: refreshes AniList-backed series (this fork's non-TheTVDB
    // anime/donghua additions) so newly-aired episodes show up, then kicks
    // off a search for any monitored episode that has aired but isn't on
    // disk yet -- the "check every day for a new episode and grab it" job.
    public class SiteSeriesSyncCommand : Command
    {
        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => false;
    }
}
