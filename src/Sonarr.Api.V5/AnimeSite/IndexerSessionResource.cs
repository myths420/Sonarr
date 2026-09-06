namespace Sonarr.Api.V5.AnimeSite;

// POST body. TokenValue: the bare cf_clearance value or a full
// "cf_clearance=...; __cf_bm=..." cookie string.
public class UpdateSessionRequest
{
    public string? TargetDomain { get; set; }
    public string? TokenValue { get; set; }
    public string? UserAgent { get; set; }
}

public class IndexerSessionUpdateResultResource
{
    public string? TargetDomain { get; set; }
    public List<string> UpdatedIndexers { get; set; } = new();
}

public class IndexerSessionStatusResource
{
    public int IndexerId { get; set; }
    public string? IndexerName { get; set; }
    public string? TargetDomain { get; set; }
    public bool HasSession { get; set; }
    public bool Expired { get; set; }
}
