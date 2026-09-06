namespace NzbDrone.Core.AnimeSite
{
    // Synthetic Series.TvdbId for a catalogue show with no AniList match.
    // Sits in its own band (900M..1B) below the AniList band and above every
    // real TheTVDB id, so TvdbId > 0 checks still hold.
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
