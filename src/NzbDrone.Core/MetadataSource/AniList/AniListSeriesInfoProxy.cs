using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MetadataSource.AniList
{
    // Serves Sonarr's Series/Episode shape from AniList's public GraphQL API
    // for series this fork added by AniList id (see AniListSeriesIds). Stands
    // in for SkyHook/TheTVDB on the add + refresh paths -- SkyHookProxy
    // delegates here when it's handed a synthetic AniList id.
    public interface IAniListSeriesInfoProxy
    {
        Tuple<Series, List<Episode>> GetSeriesInfo(int aniListId);
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

        public AniListSeriesInfoProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(int aniListId)
        {
            var media = FetchMedia(aniListId, 1);

            if (media == null)
            {
                throw new SeriesNotFoundException(AniListSeriesIds.FromAniListId(aniListId));
            }

            var airing = new List<AniListAiringNode>(media.AiringSchedule?.Nodes ?? new List<AniListAiringNode>());

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

                airing.AddRange(nodes);
                media.AiringSchedule.PageInfo = next.AiringSchedule?.PageInfo;
            }

            var series = MapSeries(media);
            var episodes = MapEpisodes(media, airing);

            return new Tuple<Series, List<Episode>>(series, episodes);
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

            var response = _httpClient.Execute(request);

            if ((int)response.StatusCode == 404)
            {
                return null;
            }

            if ((int)response.StatusCode >= 400)
            {
                throw new HttpException(request, response);
            }

            var envelope = JsonSerializer.Deserialize<AniListResponse>(response.Content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return envelope?.Data?.Media;
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

        private static List<Episode> MapEpisodes(AniListMedia media, List<AniListAiringNode> airing)
        {
            var byNumber = new Dictionary<int, Episode>();

            foreach (var node in airing.Where(n => n.Episode > 0))
            {
                var airDateUtc = DateTimeOffset.FromUnixTimeSeconds(node.AiringAt).UtcDateTime;

                byNumber[node.Episode] = new Episode
                {
                    SeasonNumber = 1,
                    EpisodeNumber = node.Episode,
                    AbsoluteEpisodeNumber = node.Episode,
                    Title = $"Episode {node.Episode}",
                    AirDate = airDateUtc.ToString(Episode.AIR_DATE_FORMAT),
                    AirDateUtc = airDateUtc,
                    Monitored = true,
                    Runtime = media.Duration ?? 0
                };
            }

            // AniList often exposes only a partial airing schedule (or none,
            // for finished shows). Fill the gap up to the known/estimated
            // episode count so the season isn't missing rows.
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
                    SeasonNumber = 1,
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
            public List<AniListAiringNode> Nodes { get; set; }
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
