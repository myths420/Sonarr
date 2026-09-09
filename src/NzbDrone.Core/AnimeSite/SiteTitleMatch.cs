using System.Collections.Generic;
using System.Text.RegularExpressions;
using NzbDrone.Core.MetadataSource.AniList;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.AnimeSite
{
    // Cross-site catalogue matching. The scraper sites name the same show
    // differently -- a trailing year, "New", "Sub Indo", "Multi Subtitle" --
    // so a catalogue row from one site doesn't link to a series added from
    // another. Strip that noise, the season suffix, and everything
    // CleanSeriesTitle drops, down to one comparable key.
    public static class SiteTitleMatch
    {
        private static readonly Regex TrailingYear = new Regex(
            @"\s*\(?(?:19|20)\d{2}\)?\s*$", RegexOptions.Compiled);

        private static readonly Regex TrailingNoise = new Regex(
            @"\s+(?:new|uncensored|remastered|remaster|donghua|multi[\s-]*sub(?:title)?s?|sub[\s-]*indo|indo[\s-]*sub|subbed|dubbed|final)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Normalised equality key; empty string when nothing usable is left.
        public static string Key(string title)
        {
            var text = SeasonTitleParser.Parse(title ?? string.Empty).BaseTitle;

            string previous;
            do
            {
                previous = text;
                text = TrailingYear.Replace(text, string.Empty).Trim();
                text = TrailingNoise.Replace(text, string.Empty).Trim();
            }
            while (text != previous && text.Length > 2);

            return text.CleanSeriesTitle();
        }

        // Every AniList id that identifies this series: the one encoded in
        // its synthetic tvdb id, plus any recorded for folded seasons.
        public static IEnumerable<int> AniListIds(Series series)
        {
            if (series == null)
            {
                yield break;
            }

            if (AniListSeriesIds.IsAniListId(series.TvdbId))
            {
                yield return AniListSeriesIds.ToAniListId(series.TvdbId);
            }

            foreach (var id in series.AniListIds)
            {
                yield return id;
            }
        }
    }
}
