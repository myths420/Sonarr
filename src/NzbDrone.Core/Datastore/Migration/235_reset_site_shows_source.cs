using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // SiteShow.SourceListId now holds an AnimeSite indexer id (was an import
    // list id). Clear the table; the Sites Refresh action repopulates it.
    [Migration(235)]
    public class reset_site_shows_source : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.Sql("DELETE FROM \"SiteShows\"");
        }
    }
}
