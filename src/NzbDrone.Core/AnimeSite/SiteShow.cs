using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.AnimeSite
{
    // One show from an AnimeSite indexer's catalogue. Identity is
    // (SourceListId, Slug); Slug is the last path segment of Url. Metadata
    // fields are best-effort and may be empty.
    public class SiteShow : ModelBase
    {
        public int SourceListId { get; set; }
        public string Slug { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string PosterUrl { get; set; }
        public string Overview { get; set; }
        public int Year { get; set; }
        public int Episodes { get; set; }
        public string Status { get; set; }
        public string Genres { get; set; }
        public int AniListId { get; set; }
        public DateTime LastSyncTime { get; set; }
    }
}
