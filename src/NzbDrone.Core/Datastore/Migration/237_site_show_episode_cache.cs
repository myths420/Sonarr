using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // Cache each catalogue show's scraped episode list (number + title +
    // parsed air date) so an AniList-backed series can be topped up with
    // real air dates -- without a live scrape on every Refresh -- and newly
    // aired episodes get picked up automatically even while AniList lags.
    [Migration(237)]
    public class site_show_episode_cache : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("SiteShows")
                .AddColumn("EpisodesJson").AsString().Nullable()
                .AddColumn("LastEpisodeSync").AsDateTimeOffset().Nullable();
        }
    }
}
