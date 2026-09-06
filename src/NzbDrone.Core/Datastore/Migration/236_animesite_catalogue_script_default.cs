using System.Collections.Generic;
using System.Data;
using Dapper;
using FluentMigrator;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // The AnimeSite indexer's Catalogue Script used to be saved with a copy
    // of the built-in default baked in, so it never picked up improvements
    // to that default (e.g. handling /anime/{slug}/ style sitemap URLs).
    // Clear it on existing indexers -- an empty value now means "use the
    // current built-in default", and only a user-written script is stored.
    [Migration(236)]
    public class animesite_catalogue_script_default : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection(ClearCatalogueScript);
        }

        private void ClearCatalogueScript(IDbConnection conn, IDbTransaction tran)
        {
            var updated = new List<object>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tran;
                cmd.CommandText = "SELECT \"Id\", \"Settings\" FROM \"Indexers\" WHERE \"Implementation\" = 'AnimeSiteIndexer'";

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var id = reader.GetInt32(0);
                        var settings = Json.Deserialize<JObject>(reader.GetString(1));

                        settings["catalogueScript"] = string.Empty;

                        updated.Add(new { Settings = settings.ToJson(), Id = id });
                    }
                }
            }

            if (updated.Count > 0)
            {
                conn.Execute("UPDATE \"Indexers\" SET \"Settings\" = @Settings WHERE \"Id\" = @Id", updated, transaction: tran);
            }
        }
    }
}
