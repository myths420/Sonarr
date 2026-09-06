using NzbDrone.Core.AnimeSite;
using Sonarr.Http.REST;

namespace Sonarr.Api.V5.AnimeSite;

public class SiteShowResource : RestResource
{
    public int SourceListId { get; set; }
    public string? Slug { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? PosterUrl { get; set; }
    public string? Overview { get; set; }
    public int Year { get; set; }
    public int Episodes { get; set; }
    public string? Status { get; set; }
    public List<string> Genres { get; set; } = new();
    public int AniListId { get; set; }

    // Set by the controller when a matching library series exists.
    public int? SeriesId { get; set; }
    public string? SeriesTitleSlug { get; set; }
}

// Body for POST /siteshow/{id}/add. All optional; omitted fields use the
// first configured root folder / quality profile.
public class SiteShowAddResource
{
    public string? RootFolderPath { get; set; }
    public int? QualityProfileId { get; set; }
    public bool SearchForMissingEpisodes { get; set; }
}

public static class SiteShowResourceMapper
{
    public static SiteShowResource? ToResource(this SiteShow? model)
    {
        if (model == null)
        {
            return null;
        }

        return new SiteShowResource
        {
            Id = model.Id,
            SourceListId = model.SourceListId,
            Slug = model.Slug,
            Title = model.Title,
            Url = model.Url,

            // The locally-cached poster endpoint; null when there's no poster.
            PosterUrl = string.IsNullOrWhiteSpace(model.PosterUrl)
                ? null
                : $"/api/v5/siteshow/{model.Id}/poster",
            Overview = model.Overview,
            Year = model.Year,
            Episodes = model.Episodes,
            Status = model.Status,
            Genres = (model.Genres ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            AniListId = model.AniListId
        };
    }

    public static List<SiteShowResource> ToResource(this IEnumerable<SiteShow> models)
    {
        return models.Select(ToResource).ToList()!;
    }
}
