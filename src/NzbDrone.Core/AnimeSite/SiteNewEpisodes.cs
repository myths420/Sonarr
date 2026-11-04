using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.AnimeSite
{
    // What counts as a "new" episode of a Site/AniList-backed series: the
    // ones the automatic search should keep trying for, and that a Refresh
    // must not unmonitor. Two cases:
    //  - aired in the last two weeks (or not aired yet), and
    //  - aired after the newest episode already on disk -- i.e. the show
    //    carrying on from where the library stopped, so episodes missed
    //    during an outage are still picked up -- but not older than
    //    ContinuationWindow, so a long-dead back catalogue isn't swept in.
    // Gaps older than the newest file stay the manual "Download missing".
    public static class SiteNewEpisodes
    {
        public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);
        public static readonly TimeSpan ContinuationWindow = TimeSpan.FromDays(120);

        public static DateTime? LastOnDiskAirDate(IEnumerable<Episode> episodes)
        {
            var dates = episodes
                .Where(e => e.HasFile && e.AirDateUtc.HasValue)
                .Select(e => e.AirDateUtc.Value)
                .ToList();

            return dates.Count == 0 ? (DateTime?)null : dates.Max();
        }

        public static bool IsNew(Episode episode, DateTime? lastOnDiskAirDate, DateTime now)
        {
            if (!episode.AirDateUtc.HasValue)
            {
                return false;
            }

            var airDate = episode.AirDateUtc.Value;
            var age = now - airDate;

            if (age <= RecentWindow)
            {
                return true;
            }

            return lastOnDiskAirDate.HasValue && airDate > lastOnDiskAirDate.Value && age <= ContinuationWindow;
        }
    }
}
