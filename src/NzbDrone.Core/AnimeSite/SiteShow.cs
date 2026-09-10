using System;
using System.Collections.Generic;
using System.Text.Json;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.AnimeSite
{
    // One show from an AnimeSite indexer's catalogue. Identity is
    // (SourceListId, Slug); Slug is the last path segment of Url. Metadata
    // fields are best-effort and may be empty.
    public class SiteShow : ModelBase
    {
        public int SourceListId { get; set; }
        public string Slug { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string PosterUrl { get; set; }
        public string Overview { get; set; }
        public int Year { get; set; }
        public int Episodes { get; set; }
        public string Status { get; set; }
        public string Genres { get; set; }
        public int AniListId { get; set; }
        public DateTime LastSyncTime { get; set; }

        // Manual override: a library series this row belongs to, and which
        // of its seasons, set from the Sites show detail view when the
        // auto-match can't line up (abbreviations, typos, odd season names).
        // 0 = not set.
        public int MappedSeriesId { get; set; }
        public int MappedSeason { get; set; }

        // Cached scraped episode list (see SiteShowEpisode) and when it was
        // last refreshed. Kept current by the Sites sync so an AniList-backed
        // series can be topped up with real air dates without a live scrape.
        public string EpisodesJson { get; set; }
        public DateTime LastEpisodeSync { get; set; }

        public List<SiteShowEpisode> GetCachedEpisodes()
        {
            if (string.IsNullOrWhiteSpace(EpisodesJson))
            {
                return new List<SiteShowEpisode>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<SiteShowEpisode>>(EpisodesJson) ?? new List<SiteShowEpisode>();
            }
            catch (JsonException)
            {
                return new List<SiteShowEpisode>();
            }
        }

        public void SetCachedEpisodes(List<SiteShowEpisode> episodes)
        {
            EpisodesJson = episodes == null || episodes.Count == 0
                ? null
                : JsonSerializer.Serialize(episodes);
            LastEpisodeSync = DateTime.UtcNow;
        }
    }

    // One scraped episode of a catalogue show. AirDateUtc is parsed from the
    // episode title ("... December 12, 2025") when present.
    public class SiteShowEpisode
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public DateTime? AirDateUtc { get; set; }
    }
}
