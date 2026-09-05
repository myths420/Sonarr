namespace NzbDrone.Core.MetadataSource.AniList
{
    // This fork adds anime/donghua series that only exist on AniList, not
    // TheTVDB. Rather than thread a second id through the whole codebase,
    // an AniList-backed series stores a synthetic value in Series.TvdbId:
    // Offset + aniListId. TheTVDB's real ids are 6-7 digits (well under the
    // offset) and AniList ids are 5-6 digits, so the ranges never collide,
    // and every "is this series from TheTVDB" check (TvdbId > 0) still holds.
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
