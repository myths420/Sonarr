using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(234)]
    public class add_site_shows : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.TableForModel("SiteShows")
                .WithColumn("SourceListId").AsInt32().NotNullable()
                .WithColumn("Slug").AsString().NotNullable()
                .WithColumn("Title").AsString().NotNullable()
                .WithColumn("Url").AsString().NotNullable()
                .WithColumn("PosterUrl").AsString().Nullable()
                .WithColumn("Overview").AsString().Nullable()
                .WithColumn("Year").AsInt32().Nullable()
                .WithColumn("Episodes").AsInt32().Nullable()
                .WithColumn("Status").AsString().Nullable()
                .WithColumn("Genres").AsString().Nullable()
                .WithColumn("AniListId").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("LastSyncTime").AsDateTimeOffset().Nullable();

            Create.Index()
                .OnTable("SiteShows")
                .OnColumn("SourceListId").Ascending()
                .OnColumn("Slug").Ascending()
                .WithOptions().Unique();
        }
    }
}
