using NzbDrone.Core.Indexers.AnimeSite;

namespace Sonarr.Api.V5.AnimeSite;

public class SiteShowReleaseResource
{
    public string? Title { get; set; }
    public string? Url { get; set; }
}

public static class SiteShowReleaseResourceMapper
{
    public static SiteShowReleaseResource ToResource(this ResolvedRelease model)
    {
        return new SiteShowReleaseResource
        {
            Title = model.Title,
            Url = model.Url
        };
    }

    public static List<SiteShowReleaseResource> ToResource(this IEnumerable<ResolvedRelease> models)
    {
        return models.Select(ToResource).ToList();
    }
}
