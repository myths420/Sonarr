using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // SiteShow.SourceListId changed meaning: it used to hold an AnimeSite
    // *import list* id, it now holds an AnimeSite *indexer* id (one indexer
    // = one site). Existing rows carry the old kind of id, so clear the
    // table -- the Sites Refresh action repopulates it from the indexer.
    [Migration(235)]
    public class reset_site_shows_source : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.Sql("DELETE FROM \"SiteShows\"");
        }
    }
}
