using System;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using NLog;
using NzbDrone.Core.Indexers.AnimeSite;

namespace NzbDrone.Core.AnimeSite
{
    // Fallback metadata: scrape the poster / synopsis / genres straight off
    // the show's own page on the site. Lower quality than AniList (no
    // reliable year, genres vary by theme) but it's always available and
    // has no third-party dependency -- the important part, the poster,
    // comes from the same page the catalogue was built from. Port of
    // main.py's extract_anime_metadata.
    public interface ISiteScrapeMetadataProvider
    {
        ShowMetadata ScrapeFromPage(string showUrl, AnimeSiteFetchOptions fetch = null);
    }

    public class SiteScrapeMetadataProvider : ISiteScrapeMetadataProvider
    {
        private readonly IAnimeSiteFetcher _fetcher;
        private readonly Logger _logger;

        public SiteScrapeMetadataProvider(IAnimeSiteFetcher fetcher, Logger logger)
        {
            _fetcher = fetcher;
            _logger = logger;
        }

        public ShowMetadata ScrapeFromPage(string showUrl, AnimeSiteFetchOptions fetch = null)
        {
            if (string.IsNullOrWhiteSpace(showUrl))
            {
                return null;
            }

            try
            {
                var html = _fetcher.GetHtml(showUrl, null, fetch ?? AnimeSiteFetchOptions.Direct);
                var doc = ParseHtml(html);

                var poster = FirstNonEmpty(
                    doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content"),
                    doc.QuerySelector(".thumb img, .ts-post-image")?.GetAttribute("data-src"),
                    doc.QuerySelector(".thumb img, .ts-post-image")?.GetAttribute("src"));

                // A base64 data: URI is a lazy-load placeholder, not a poster.
                if (poster != null && poster.StartsWith("data:"))
                {
                    poster = null;
                }

                var overview = doc.QuerySelector(".entry-content, .desc, [itemprop='description']")?.TextContent?.Trim();
                if (!string.IsNullOrEmpty(overview) && overview.Length > 1200)
                {
                    overview = overview[..1200].TrimEnd() + "…";
                }

                var genres = doc.QuerySelectorAll(".genxinf a, .genxed a, .mgen a, .sgeneros a")
                    .Select(a => a.TextContent?.Trim())
                    .Where(g => !string.IsNullOrEmpty(g))
                    .Distinct()
                    .Take(6)
                    .ToList();

                var year = ExtractYear(doc);

                if (string.IsNullOrEmpty(poster) && string.IsNullOrEmpty(overview) && genres.Count == 0)
                {
                    return null;
                }

                return new ShowMetadata
                {
                    PosterUrl = poster,
                    Overview = overview,
                    Genres = genres,
                    Year = year
                };
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Site metadata scrape failed for {0}", showUrl);
                return null;
            }
        }

        private static int ExtractYear(IDocument doc)
        {
            var candidates = new[]
            {
                doc.QuerySelector("time[datetime]")?.GetAttribute("datetime"),
                doc.QuerySelector(".year a, .year, .released, [itemprop='dateCreated']")?.TextContent,
                doc.QuerySelector(".spe")?.TextContent
            };

            foreach (var text in candidates)
            {
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var match = Regex.Match(text, @"(19|20)\d{2}");
                if (match.Success && int.TryParse(match.Value, out var year))
                {
                    return year;
                }
            }

            return 0;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        private static IDocument ParseHtml(string html)
        {
            var config = AngleSharp.Configuration.Default;
            var context = BrowsingContext.New(config);
            return context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();
        }
    }
}
