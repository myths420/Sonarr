namespace NzbDrone.Core.AnimeSite
{
    // Synthetic Series.TvdbId for a catalogue show that has no AniList
    // match -- built purely from what the site itself gives us (title +
    // scraped episode list). Sits in its own band below the AniList band
    // (see MetadataSource.AniList.AniListSeriesIds, >= 1_000_000_000) and
    // well above every real TheTVDB id, so "is this from TheTVDB"
    // (TvdbId > 0) still holds and the two synthetic kinds never collide.
    public static class SiteSeriesIds
    {
        public const int Offset = 900_000_000;
        public const int Ceiling = 1_000_000_000;

        public static bool IsSiteId(int seriesId)
        {
            return seriesId >= Offset && seriesId < Ceiling;
        }

        public static int ToSiteShowId(int seriesId)
        {
            return seriesId - Offset;
        }

        public static int FromSiteShowId(int siteShowId)
        {
            return Offset + siteShowId;
        }
    }
}
