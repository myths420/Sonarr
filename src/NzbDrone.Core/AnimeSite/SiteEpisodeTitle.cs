using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.AnimeSite
{
    // The scraper sites put the upload date in the episode title, e.g.
    // "... Episode 12 Subbed Sub December 12, 2025". Pull it out so the
    // synthetic episode gets a real air date.
    public static class SiteEpisodeTitle
    {
        private static readonly Regex DateInTitle = new Regex(
            @"(January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},\s+\d{4}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static DateTime? ParseAirDate(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var match = DateInTitle.Match(title);
            if (match.Success &&
                DateTime.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                return date;
            }

            return null;
        }
    }
}
