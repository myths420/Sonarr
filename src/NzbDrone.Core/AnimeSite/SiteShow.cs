using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.AnimeSite
{
    // One show as seen on a configured AnimeSite import list's catalogue --
    // deliberately NOT keyed on TVDB (most donghua/shorts this fork targets
    // aren't in TVDB at all, see animesite-fork-status notes). Identity is
    // (SourceListId, Slug), Slug being the last path segment of Url. Poster/
    // Overview/etc are filled in best-effort by IShowMetadataProvider and
    // may stay empty for shows no metadata source recognizes -- the catalogue
    // still lists them by title alone.
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
