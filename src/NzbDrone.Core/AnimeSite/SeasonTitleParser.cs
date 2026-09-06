using System.Text.RegularExpressions;

namespace NzbDrone.Core.AnimeSite
{
    // Splits a catalogue title like "Battle Through The Heavens Season 4"
    // into ("Battle Through The Heavens", 4) so seasons of one show fold
    // into a single Series instead of a poster each.
    public static class SeasonTitleParser
    {
        // Each captures the season number in group 1 and everything from the
        // match onward is dropped: "Season 4", "Season 4 Final Season",
        // "2nd Season", "Part 2", "S3", trailing "... 2".
        private static readonly Regex[] SeasonPatterns =
        {
            new Regex(@"\s+season\s+(\d{1,2})\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s+(\d{1,2})(?:st|nd|rd|th)\s+season\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s+part\s+(\d{1,2})\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s+s(\d{1,2})\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s+(\d{1,2})\s*$", RegexOptions.Compiled),
        };

        public struct Result
        {
            public string BaseTitle;
            public int Season;
            public bool HasSeason => Season >= 2;
        }

        public static Result Parse(string title)
        {
            var t = (title ?? string.Empty).Trim();
            if (t.Length == 0)
            {
                return new Result { BaseTitle = t, Season = 1 };
            }

            foreach (var pattern in SeasonPatterns)
            {
                var m = pattern.Match(t);
                if (!m.Success)
                {
                    continue;
                }

                if (!int.TryParse(m.Groups[1].Value, out var n) || n < 2 || n > 20)
                {
                    continue;
                }

                var baseTitle = t.Substring(0, m.Index).Trim().TrimEnd('-', ':').Trim();
                if (baseTitle.Length < 2)
                {
                    continue;
                }

                return new Result { BaseTitle = baseTitle, Season = n };
            }

            // "Season 1" / "S1" with no higher season is still just the base show.
            var stripOne = Regex.Replace(t, @"\s+(season\s+1|s1)\b\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
            return new Result { BaseTitle = stripOne, Season = 1 };
        }
    }
}
