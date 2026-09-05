using NzbDrone.Core.ImportLists.AnimeSite;

namespace Sonarr.Api.V5.AnimeSite;

public class SiteShowEpisodeResource
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
}

public static class SiteShowEpisodeResourceMapper
{
    public static SiteShowEpisodeResource ToResource(this AnimeSiteEpisodeEntry model)
    {
        return new SiteShowEpisodeResource
        {
            Number = model.Number,
            Title = model.Title,
            Url = model.Url
        };
    }

    public static List<SiteShowEpisodeResource> ToResource(this IEnumerable<AnimeSiteEpisodeEntry> models)
    {
        return models.Select(ToResource).ToList();
    }
}
