using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.AnimeSite
{
    // Poster / overview / genre lookup for a scraped show title, via
    // AniList's public GraphQL API (no key required).
    public class ShowMetadata
    {
        public int AniListId { get; set; }
        public string PosterUrl { get; set; }
        public string Overview { get; set; }
        public int Year { get; set; }
        public int Episodes { get; set; }
        public string Status { get; set; }
        public List<string> Genres { get; set; } = new();
    }

    public interface IShowMetadataProvider
    {
        ShowMetadata Lookup(string title);
    }

    public class AniListShowMetadataProvider : IShowMetadataProvider
    {
        private const string Endpoint = "https://graphql.anilist.co";

        // perPage:1 -- take AniList's own top match for the term.
        private const string SearchQuery = @"
            query ($search: String) {
                Page(page: 1, perPage: 1) {
                    media(search: $search, type: ANIME) {
                        id
                        description(asHtml: false)
                        episodes
                        status
                        startDate { year }
                        coverImage { large }
                        genres
                    }
                }
            }";

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public AniListShowMetadataProvider(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public ShowMetadata Lookup(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            try
            {
                var body = JsonSerializer.Serialize(new { query = SearchQuery, variables = new { search = title } });

                var request = new HttpRequest(Endpoint);
                request.Method = HttpMethod.Post;
                request.Headers.ContentType = "application/json";
                request.Headers.Accept = "application/json";
                request.SetContent(body);

                var response = _httpClient.Execute(request);
                var envelope = JsonSerializer.Deserialize<AniListSearchResponse>(response.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var media = envelope?.Data?.Page?.Media?.FirstOrDefault();

                if (media == null)
                {
                    return null;
                }

                return new ShowMetadata
                {
                    AniListId = media.Id,
                    PosterUrl = media.CoverImage?.Large,
                    Overview = media.Description,
                    Year = media.StartDate?.Year ?? 0,
                    Episodes = media.Episodes ?? 0,
                    Status = media.Status,
                    Genres = media.Genres ?? new List<string>()
                };
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "AniList metadata lookup failed for '{0}'", title);
                return null;
            }
        }

        private class AniListSearchResponse
        {
            public AniListSearchData Data { get; set; }
        }

        private class AniListSearchData
        {
            public AniListPage Page { get; set; }
        }

        private class AniListPage
        {
            public List<AniListMedia> Media { get; set; }
        }

        private class AniListMedia
        {
            public int Id { get; set; }
            public string Description { get; set; }
            public int? Episodes { get; set; }
            public string Status { get; set; }

            [JsonPropertyName("startDate")]
            public AniListDate StartDate { get; set; }

            [JsonPropertyName("coverImage")]
            public AniListCoverImage CoverImage { get; set; }
            public List<string> Genres { get; set; }
        }

        private class AniListDate
        {
            public int? Year { get; set; }
        }

        private class AniListCoverImage
        {
            public string Large { get; set; }
        }
    }
}
