using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MetadataSource.AniList
{
    // Builds the Series/Episode shape from AniList's GraphQL API.
    // SkyHookProxy.GetSeriesInfo delegates here for synthetic AniList ids
    // (see AniListSeriesIds).
    public interface IAniListSeriesInfoProxy
    {
        Tuple<Series, List<Episode>> GetSeriesInfo(int aniListId);

        // Multi-season: one AniList Media per season, earliest first ->
        // Season 1..N of a single Series. Used when a catalogue show and
        // its "Season 2/3/..." siblings were folded together.
        Tuple<Series, List<Episode>> GetSeriesInfo(IReadOnlyList<int> aniListIds);
    }

    public class AniListSeriesInfoProxy : IAniListSeriesInfoProxy
    {
        private const string Endpoint = "https://graphql.anilist.co";
        private const int MaxSchedulePages = 12;

        private const string Query = @"
            query ($id: Int, $page: Int) {
                Media(id: $id, type: ANIME) {
                    id
                    title { romaji english native }
                    description(asHtml: false)
                    episodes
                    duration
                    status
                    genres
                    startDate { year month day }
                    endDate { year month day }
                    coverImage { extraLarge large }
                    bannerImage
                    nextAiringEpisode { episode airingAt }
                    airingSchedule(page: $page, perPage: 50) {
                        pageInfo { hasNextPage }
                        nodes { episode airingAt }
                    }
                }
            }";

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly string _cacheFolder;

        public AniListSeriesInfoProxy(IHttpClient httpClient, IAppFolderInfo appFolderInfo, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _cacheFolder = Path.Combine(appFolderInfo.AppDataFolder, "anilist-cache");
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(int aniListId)
        {
            var media = FetchMediaWithSchedule(aniListId);
            if (media == null)
            {
                throw new SeriesNotFoundException(AniListSeriesIds.FromAniListId(aniListId));
            }

            var series = MapSeries(media);
            var episodes = MapEpisodes(media, media.AiringSchedule?.Nodes ?? new List<AniListAiringNode>(), 1);

            return new Tuple<Series, List<Episode>>(series, episodes);
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(IReadOnlyList<int> aniListIds)
        {
            if (aniListIds == null || aniListIds.Count <= 1)
            {
                return GetSeriesInfo(aniListIds is { Count: 1 } ? aniListIds[0] : throw new SeriesNotFoundException(0));
            }

            var media = new List<AniListMedia>();
            foreach (var id in aniListIds)
            {
                var m = FetchMediaWithSchedule(id);
                if (m != null)
                {
                    media.Add(m);
                }
            }

            if (media.Count == 0)
            {
                throw new SeriesNotFoundException(AniListSeriesIds.FromAniListId(aniListIds[0]));
            }

            if (media.Count == 1)
            {
                return new Tuple<Series, List<Episode>>(MapSeries(media[0]),
                    MapEpisodes(media[0], media[0].AiringSchedule?.Nodes ?? new List<AniListAiringNode>(), 1));
            }

            // Earliest-aired first -> Season 1..N.
            media.Sort((a, b) => Nullable.Compare(ToDate(a.StartDate), ToDate(b.StartDate)));

            var series = MapSeries(media[0]);
            series.AniListIds = new HashSet<int>(aniListIds);

            var episodes = new List<Episode>();
            for (var i = 0; i < media.Count; i++)
            {
                var seasonNumber = i + 1;
                series.Seasons.RemoveAll(s => s.SeasonNumber == seasonNumber);
                series.Seasons.Add(new Season { SeasonNumber = seasonNumber, Monitored = true });
                episodes.AddRange(MapEpisodes(media[i], media[i].AiringSchedule?.Nodes ?? new List<AniListAiringNode>(), seasonNumber));
            }

            var last = media[media.Count - 1];
            series.Status = MapStatus(last.Status);
            series.LastAired = ToDate(last.EndDate) ?? series.LastAired;

            return new Tuple<Series, List<Episode>>(series, episodes);
        }

        private AniListMedia FetchMediaWithSchedule(int aniListId)
        {
            var media = FetchMedia(aniListId, 1);
            if (media == null)
            {
                return null;
            }

            var page = 1;
            while (media.AiringSchedule?.PageInfo?.HasNextPage == true && page < MaxSchedulePages)
            {
                page++;
                var next = FetchMedia(aniListId, page);
                var nodes = next?.AiringSchedule?.Nodes;
                if (nodes == null || nodes.Count == 0)
                {
                    break;
                }

                media.AiringSchedule.Nodes.AddRange(nodes);
                media.AiringSchedule.PageInfo = next.AiringSchedule?.PageInfo;
            }

            return media;
        }

        private AniListMedia FetchMedia(int aniListId, int page)
        {
            var body = JsonSerializer.Serialize(new
            {
                query = Query,
                variables = new { id = aniListId, page }
            });

            var request = new HttpRequest(Endpoint) { Method = HttpMethod.Post };
            request.Headers.ContentType = "application/json";
            request.Headers.Accept = "application/json";
            request.SetContent(body);
            request.SuppressHttpError = true;

            HttpResponse response = null;
            try
            {
                response = _httpClient.Execute(request);
            }
            catch (Exception ex)
            {
                var cached = FromCache(aniListId, page);
                if (cached == null)
                {
                    throw;
                }

                _logger.Warn(ex, "AniList request failed for {0} (page {1}); serving cached metadata", aniListId, page);
                return cached;
            }

            if ((int)response.StatusCode == 404)
            {
                return null;
            }

            if ((int)response.StatusCode >= 400)
            {
                // AniList periodically 403s the whole API during outages.
                // Serve the last-known-good copy so a Refresh doesn't wipe
                // existing series and episode data.
                var cached = FromCache(aniListId, page);
                if (cached != null)
                {
                    _logger.Warn("AniList returned {0} for {1}; serving cached metadata", (int)response.StatusCode, aniListId);
                    return cached;
                }

                throw new HttpException(request, response);
            }

            var envelope = JsonSerializer.Deserialize<AniListResponse>(response.Content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var media = envelope?.Data?.Media;
            if (media != null)
            {
                ToCache(aniListId, page, response.Content);
            }

            return media;
        }

        private string CachePath(int aniListId, int page) => Path.Combine(_cacheFolder, $"{aniListId}-{page}.json");

        private void ToCache(int aniListId, int page, string content)
        {
            try
            {
                Directory.CreateDirectory(_cacheFolder);
                File.WriteAllText(CachePath(aniListId, page), content);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to cache AniList response for {0}", aniListId);
            }
        }

        private AniListMedia FromCache(int aniListId, int page)
        {
            try
            {
                var path = CachePath(aniListId, page);
                if (!File.Exists(path))
                {
                    return null;
                }

                var envelope = JsonSerializer.Deserialize<AniListResponse>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                return envelope?.Data?.Media;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to read cached AniList response for {0}", aniListId);
                return null;
            }
        }

        private static Series MapSeries(AniListMedia media)
        {
            var title = FirstNonEmpty(media.Title?.English, media.Title?.Romaji, media.Title?.Native)
                        ?? $"AniList {media.Id}";

            var series = new Series
            {
                TvdbId = AniListSeriesIds.FromAniListId(media.Id),
                AniListIds = new HashSet<int> { media.Id },
                Title = title,
                CleanTitle = Parser.Parser.CleanSeriesTitle(title),
                SortTitle = SeriesTitleNormalizer.Normalize(title, 0),
                TitleSlug = $"anilist-{media.Id}",
                Overview = StripHtml(media.Description),
                Status = MapStatus(media.Status),
                Network = "AniList",
                Runtime = media.Duration ?? 24,
                SeriesType = SeriesTypes.Anime,
                Genres = media.Genres ?? new List<string>(),
                Seasons = new List<Season> { new Season { SeasonNumber = 1, Monitored = true } },
                Images = new List<MediaCover.MediaCover>(),
                Ratings = new Ratings(),
                Monitored = true
            };

            var firstAired = ToDate(media.StartDate);
            if (firstAired != null)
            {
                series.FirstAired = firstAired;
                series.Year = firstAired.Value.Year;
            }

            var lastAired = ToDate(media.EndDate);
            if (lastAired != null)
            {
                series.LastAired = lastAired;
            }

            var poster = FirstNonEmpty(media.CoverImage?.ExtraLarge, media.CoverImage?.Large);
            if (poster != null)
            {
                series.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Poster, poster));
            }

            if (!string.IsNullOrWhiteSpace(media.BannerImage))
            {
                series.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Banner, media.BannerImage));
            }

            return series;
        }

        private static List<Episode> MapEpisodes(AniListMedia media, List<AniListAiringNode> airing, int seasonNumber)
        {
            var byNumber = new Dictionary<int, Episode>();

            foreach (var node in airing.Where(n => n.Episode > 0))
            {
                var airDateUtc = DateTimeOffset.FromUnixTimeSeconds(node.AiringAt).UtcDateTime;

                byNumber[node.Episode] = new Episode
                {
                    SeasonNumber = seasonNumber,
                    EpisodeNumber = node.Episode,
                    AbsoluteEpisodeNumber = node.Episode,
                    Title = $"Episode {node.Episode}",
                    AirDate = airDateUtc.ToString(Episode.AIR_DATE_FORMAT),
                    AirDateUtc = airDateUtc,
                    Monitored = true,
                    Runtime = media.Duration ?? 0
                };
            }

            // Fill in any episodes the airing schedule didn't cover, up to
            // the known/estimated count.
            var total = media.Episodes ?? 0;
            if (total == 0 && media.NextAiringEpisode?.Episode > 1)
            {
                total = media.NextAiringEpisode.Episode - 1;
            }

            if (byNumber.Count > 0)
            {
                total = Math.Max(total, byNumber.Keys.Max());
            }

            for (var n = 1; n <= total; n++)
            {
                if (byNumber.ContainsKey(n))
                {
                    continue;
                }

                byNumber[n] = new Episode
                {
                    SeasonNumber = seasonNumber,
                    EpisodeNumber = n,
                    AbsoluteEpisodeNumber = n,
                    Title = $"Episode {n}",
                    Monitored = true,
                    Runtime = media.Duration ?? 0
                };
            }

            return byNumber.Values.OrderBy(e => e.EpisodeNumber).ToList();
        }

        private static SeriesStatusType MapStatus(string status)
        {
            switch ((status ?? string.Empty).ToUpperInvariant())
            {
                case "RELEASING":
                case "HIATUS":
                    return SeriesStatusType.Continuing;
                case "NOT_YET_RELEASED":
                    return SeriesStatusType.Upcoming;
                case "FINISHED":
                case "CANCELLED":
                    return SeriesStatusType.Ended;
                default:
                    return SeriesStatusType.Continuing;
            }
        }

        private static DateTime? ToDate(AniListFuzzyDate date)
        {
            if (date?.Year is not > 0)
            {
                return null;
            }

            try
            {
                return new DateTime(date.Year.Value, date.Month is > 0 ? date.Month.Value : 1, date.Day is > 0 ? date.Day.Value : 1, 0, 0, 0, DateTimeKind.Utc);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new DateTime(date.Year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }
        }

        private static string StripHtml(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var text = Regex.Replace(value, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<.*?>", string.Empty);
            return text.Trim();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        private class AniListResponse
        {
            public AniListData Data { get; set; }
        }

        private class AniListData
        {
            public AniListMedia Media { get; set; }
        }

        private class AniListMedia
        {
            public int Id { get; set; }
            public AniListTitle Title { get; set; }
            public string Description { get; set; }
            public int? Episodes { get; set; }
            public int? Duration { get; set; }
            public string Status { get; set; }
            public List<string> Genres { get; set; }

            [JsonPropertyName("startDate")]
            public AniListFuzzyDate StartDate { get; set; }

            [JsonPropertyName("endDate")]
            public AniListFuzzyDate EndDate { get; set; }

            [JsonPropertyName("coverImage")]
            public AniListCoverImage CoverImage { get; set; }

            [JsonPropertyName("bannerImage")]
            public string BannerImage { get; set; }

            [JsonPropertyName("nextAiringEpisode")]
            public AniListAiringNode NextAiringEpisode { get; set; }

            [JsonPropertyName("airingSchedule")]
            public AniListAiringSchedule AiringSchedule { get; set; }
        }

        private class AniListTitle
        {
            public string Romaji { get; set; }
            public string English { get; set; }
            public string Native { get; set; }
        }

        private class AniListCoverImage
        {
            public string ExtraLarge { get; set; }
            public string Large { get; set; }
        }

        private class AniListFuzzyDate
        {
            public int? Year { get; set; }
            public int? Month { get; set; }
            public int? Day { get; set; }
        }

        private class AniListAiringSchedule
        {
            public AniListPageInfo PageInfo { get; set; }
            public List<AniListAiringNode> Nodes { get; set; } = new List<AniListAiringNode>();
        }

        private class AniListPageInfo
        {
            public bool HasNextPage { get; set; }
        }

        private class AniListAiringNode
        {
            public int Episode { get; set; }
            public long AiringAt { get; set; }
        }
    }
}
