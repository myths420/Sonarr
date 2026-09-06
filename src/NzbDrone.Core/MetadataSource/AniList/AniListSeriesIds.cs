namespace NzbDrone.Core.MetadataSource.AniList
{
    // An AniList-backed series stores Offset + aniListId in Series.TvdbId.
    // The range is above every real TheTVDB id, so TvdbId > 0 checks hold.
    public static class AniListSeriesIds
    {
        public const int Offset = 1_000_000_000;

        public static bool IsAniListId(int seriesId)
        {
            return seriesId >= Offset;
        }

        public static int ToAniListId(int seriesId)
        {
            return seriesId - Offset;
        }

        public static int FromAniListId(int aniListId)
        {
            return Offset + aniListId;
        }
    }
}
