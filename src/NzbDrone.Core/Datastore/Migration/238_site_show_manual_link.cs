using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // Lets a catalogue row be pinned by hand to a library series (and one
    // of its seasons) when the auto-match can't -- e.g. a site names
    // "Battle Through The Heavens Season 5" as "Btth Season 5".
    [Migration(238)]
    public class site_show_manual_link : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("SiteShows")
                .AddColumn("MappedSeriesId").AsInt32().NotNullable().WithDefaultValue(0)
                .AddColumn("MappedSeason").AsInt32().NotNullable().WithDefaultValue(0);
        }
    }
}
