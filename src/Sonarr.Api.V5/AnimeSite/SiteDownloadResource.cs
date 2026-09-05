using NzbDrone.Core.AnimeSite;

namespace Sonarr.Api.V5.AnimeSite;

public class SiteDownloadResource
{
    public string? DownloadId { get; set; }
    public int ShowId { get; set; }
    public int EpisodeNumber { get; set; }
    public string? Title { get; set; }
    public string? OutputPath { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalSize { get; set; }
    public long BytesPerSecond { get; set; }
    public DateTime StartedAt { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}

public static class SiteDownloadResourceMapper
{
    public static SiteDownloadResource ToResource(this SiteDownload model)
    {
        return new SiteDownloadResource
        {
            DownloadId = model.DownloadId,
            ShowId = model.ShowId,
            EpisodeNumber = model.EpisodeNumber,
            Title = model.Title,
            OutputPath = model.OutputPath,
            BytesDownloaded = model.BytesDownloaded,
            TotalSize = model.TotalSize,
            BytesPerSecond = model.BytesPerSecond,
            StartedAt = model.StartedAt,
            Status = model.Status.ToString(),
            Message = model.Message
        };
    }

    public static List<SiteDownloadResource> ToResource(this IEnumerable<SiteDownload> models)
    {
        return models.Select(ToResource).ToList();
    }
}
